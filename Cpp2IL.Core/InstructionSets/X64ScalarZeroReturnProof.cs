using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;
using NativeRegister = Iced.Intel.Register;
using IsilRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// A complete x64 leaf that clears every bit of XMM0 and returns. Its low lane
/// is positive zero for either unchanged scalar floating-point return type.
/// </summary>
internal static class X64ScalarZeroReturnProof
{
    private static ReadOnlySpan<byte> BodyBytes => [0x0F, 0x57, 0xC0, 0xC3];
    private static ReadOnlySpan<byte> PrefixedBodyBytes => [0x66, 0x90, 0x0F, 0x57, 0xC0, 0xC3];
    private static readonly X64CallingConventionResolver CallingConventions = new();

    internal static List<ManagedInstruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<Iced.Intel.Instruction> native)
    {
        var app = method.AppContext;
        var returnType = method.ReturnType;
        var single = ReferenceEquals(returnType, app.SystemTypes.SystemSingleType);
        var doublePrecision = ReferenceEquals(returnType, app.SystemTypes.SystemDoubleType);
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe ||
            X64UnwindProof.ForApplication(app) is not { } unwind ||
            !(single || doublePrecision) ||
            method.DeclaringType is not { Definition: { GenericContainer: null,
                HasCctor: false,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.GenericParameters.Count != 0 || owner.Attributes != owner.DefaultAttributes ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: var rawReturn,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            rawReturn != (single ? Il2CppTypeEnum.IL2CPP_TYPE_R4 : Il2CppTypeEnum.IL2CPP_TYPE_R8) ||
            !ReferenceEquals(returnType, method.DefaultReturnType) || method.OverrideReturnType != null ||
            method.IsVirtual || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.Parameters.Count != 0 ||
            method.GenericParameters.Count != 0 ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall |
                                      MethodImplAttributes.Synchronized)) != 0 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                requireUniqueBinding: false) ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings.Count != new HashSet<MethodAnalysisContext>(bindings).Count ||
            bindings.Any(binding => binding.UnderlyingPointer != method.UnderlyingPointer) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            CallingConventions.ReturnsViaHiddenBuffer(method) ||
            CallingConventions.ReturnRegister(method).Name != "xmm0" ||
            !(method.IsStatic
                ? CallingConventions.ResolveForManaged(method) is [IsilRegister { Name: "rcx" }]
                : CallingConventions.ResolveForManaged(method) is
                    [IsilRegister { Name: "rcx" }, IsilRegister { Name: "rdx" }]) ||
            method.UnderlyingPointer == 0 ||
            !MatchesBody(native, method.UnderlyingPointer) ||
            !HasAuthenticatedBody(method, native, pe, unwind) ||
            X86CallerExceptionRegionProof.Check(method, native, new HashSet<ulong>()) != null)
            return null;

        IOperand value = single ? new FloatLiteral(0f) : new DoubleLiteral(0d);
        return [new ManagedInstruction(0, OpCode.Return, value) { NativeAddress = native[^1].IP }];
    }

    private static bool HasAuthenticatedBody(MethodAnalysisContext method,
        IReadOnlyList<Iced.Intel.Instruction> native, PE pe, X64UnwindProof.Index unwind)
    {
        try
        {
            // Decode the file-backed body independently. Never refresh a nonempty
            // cache: a changed cached instruction must fail PE correlation.
            var bytes = native.Count == 3 ? PrefixedBodyBytes : BodyBytes;
            if (X64NativeInstructionReader.ReadFramelessLeaf(method, native.Count,
                    bytes.Length) is not { } actual || !native.SequenceEqual(actual) ||
                !method.RawBytes.AsSpan()[..bytes.Length].SequenceEqual(bytes))
                return false;

            var start = method.UnderlyingPointer;
            var bodyEnd = native[^1].NextIP;
            if (bodyEnd > ulong.MaxValue - 15)
                return false;
            var paddedEnd = (bodyEnd + 15) & ~15UL;
            var length = checked((int)(paddedEnd - start));
            var offset = pe.MapVirtualAddressToRaw(start, false);
            var image = pe.GetRawBinaryContent();
            if (offset < 0 || offset > image.Length - length)
                return false;
            var extent = image.Slice(checked((int)offset), length);
            var sharedLength = Math.Min(method.RawBytes.Length, length);
            return method.RawBytes.AsSpan()[..sharedLength].SequenceEqual(extent[..sharedLength]) &&
                   X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind, extent, start) &&
                   unwind.IsUnaffectedByBaseRelocation(start, checked((uint)length)) &&
                   X64NativePaddingProof.HasInt3Padding(pe, bodyEnd, paddedEnd) &&
                   unwind.ClassifySpan(start, paddedEnd) is
                       { Kind: X64UnwindProof.SpanKind.NoEntry, Start: var provedStart, End: var provedEnd } &&
                   provedStart == start && provedEnd == paddedEnd &&
                   !method.AppContext.MethodsByAddress.Keys.Any(address => address > start && address < paddedEnd);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    internal static bool MatchesBody(IReadOnlyList<Iced.Intel.Instruction> native, ulong start)
    {
        if (native.Count is not (2 or 3) || start > ulong.MaxValue - 6)
            return false;
        var prefixLength = 0;
        if (native.Count == 3)
        {
            var nop = native[0];
            if (nop.IP != start || nop.Code != Code.Nopw || nop.OpCount != 0 ||
                nop.Length != 2 || nop.CodeSize != CodeSize.Code64 || !Unprefixed(nop))
                return false;
            prefixLength = 2;
        }
        var clear = native[^2];
        var ret = native[^1];
        return clear.IP == start + (ulong)prefixLength && clear.NextIP == ret.IP &&
        clear.Length == 3 && ret.Length == 1 && ret.NextIP == start + (ulong)prefixLength + 4 &&
        clear.CodeSize == CodeSize.Code64 && ret.CodeSize == CodeSize.Code64 &&
        clear.Code == Code.Xorps_xmm_xmmm128 && clear.OpCount == 2 &&
        clear.Op0Kind == OpKind.Register && clear.Op1Kind == OpKind.Register &&
        clear.Op0Register == NativeRegister.XMM0 && clear.Op1Register == NativeRegister.XMM0 &&
        ret.Code == Code.Retnq && ret.OpCount == 0 &&
        Unprefixed(clear) && Unprefixed(ret);
    }

    private static bool Unprefixed(Iced.Intel.Instruction instruction) =>
        !instruction.HasLockPrefix && !instruction.HasRepPrefix && !instruction.HasRepnePrefix &&
        instruction.SegmentPrefix == NativeRegister.None;
}
