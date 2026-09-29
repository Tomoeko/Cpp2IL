using System;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds an entry scalar comparison to one incoming scalar argument and one
/// ordinary this-relative field read. This does not infer arbitrary pointers,
/// aggregate lanes, floating status registers or synchronization semantics.
/// </summary>
internal static class X64FloatingFieldOperandProof
{
    internal const string EvidenceKey = "X64FloatingFieldOperandProof.EntryRead";

    internal sealed record Evidence(FieldAnalysisContext Field, int Width,
        int ParameterIndex, NativeInstruction Comparison);

    internal static Evidence? GetEvidence(MethodAnalysisContext method) =>
        method.GetExtraData<Evidence>(EvidenceKey);

    internal static Evidence? Find(MethodAnalysisContext? method, NativeInstruction comparison)
    {
        if (method is not { Definition: not null, DeclaringType: { Definition: not null }, AppContext: { } app })
            return null;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
            X64UnwindProof.ForApplication(app) is not { } unwind ||
            comparison.IP != method.UnderlyingPointer ||
            !TryReadWidth(comparison, out var width, out var offset) ||
            method.DeclaringType is not { Definition: { GenericContainer: null,
                PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance ||
            owner.GenericParameters.Count != 0 || owner.Name != owner.DefaultName ||
            owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes ||
            !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            method.IsStatic || method.IsVirtual || method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName || method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method))
            return null;

        var type = width == 32 ? app.SystemTypes.SystemSingleType : app.SystemTypes.SystemDoubleType;
        var kind = width == 32 ? Il2CppTypeEnum.IL2CPP_TYPE_R4 : Il2CppTypeEnum.IL2CPP_TYPE_R8;
        var abi = new X64CallingConventionResolver().ResolveForParameters(method);
        if (abi.FirstOrDefault() is not ISIL.Register receiver ||
            receiver.Number != new ISIL.Register(null, "rcx").Number)
            return null;
        var leftRegister = new ISIL.Register(null, X86Utils.GetRegisterName(comparison.Op0Register));
        var parameters = method.Parameters.Where(parameter =>
            parameter.ParameterIndex + 1 < abi.Length &&
            abi[parameter.ParameterIndex + 1] is ISIL.Register register &&
            register.Number == leftRegister.Number).ToArray();
        if (parameters is not [{ } parameter] ||
            parameter.ParameterIndex < 0 ||
            !ReferenceEquals(parameter.DeclaringMethod, method) || parameter.IsRef ||
            parameter.OverrideParameterType != null ||
            parameter.Attributes != parameter.DefaultAttributes ||
            !ReferenceEquals(parameter.ParameterType, type) ||
            !ReferenceEquals(parameter.DefaultParameterType, type) ||
            parameter.Definition?.RawType is not { Type: var parameterKind,
                NumMods: 0, Byref: 0, Pinned: 0 } || parameterKind != kind)
            return null;

        var fields = owner.Fields.Where(field => !field.IsStatic && field.Offset == offset).ToArray();
        if (fields is not [{ } field] || field.Name != field.DefaultName ||
            !ReferenceEquals(field.DeclaringType, owner) ||
            !ReferenceEquals(field.BackingData?.Field.DeclaringType, owner.Definition) ||
            !ReferenceEquals(field.FieldType, type) ||
            field.BackingData?.Field.RawFieldType is not { Type: var fieldKind,
                NumMods: 0, Byref: 0, Pinned: 0 } || fieldKind != kind ||
            !NarrowFieldEqualityProof.HasUnchangedFloatingFieldLayout(new FieldReference(field,
                new LocalVariable("proved-owner", receiver, owner), offset), width))
            return null;

        if (method.RawBytes.Length < comparison.Length ||
            X86Utils.Iterate(method).FirstOrDefault() != comparison ||
            !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind,
                method.RawBytes.AsSpan()[..comparison.Length], comparison.IP) ||
            !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, comparison.IP, (uint)comparison.Length) ||
            Enumerable.Range(1, comparison.Length - 1).Any(delta =>
                app.MethodsByAddress.ContainsKey(comparison.IP + (ulong)delta)))
            return null;
        return new Evidence(field, width, parameter.ParameterIndex, comparison);
    }

    internal static bool TryReadWidth(NativeInstruction comparison, out int width, out int offset)
    {
        width = comparison.Code is Code.Comiss_xmm_xmmm32 or Code.Ucomiss_xmm_xmmm32 ? 32 :
            comparison.Code is Code.Comisd_xmm_xmmm64 or Code.Ucomisd_xmm_xmmm64 ? 64 : 0;
        offset = 0;
        if (width == 0 || comparison.IsInvalid || comparison.CodeSize != CodeSize.Code64 ||
            comparison.OpCount != 2 || comparison.Op0Kind != OpKind.Register ||
            (int)comparison.Op0Register < (int)NativeRegister.XMM0 ||
            (int)comparison.Op0Register > (int)NativeRegister.XMM3 ||
            comparison.Op1Kind != OpKind.Memory || comparison.MemoryBase != NativeRegister.RCX ||
            comparison.MemoryIndex != NativeRegister.None || comparison.MemoryIndexScale != 1 ||
            comparison.MemorySize.GetSize() != width / 8 ||
            comparison.MemoryDisplacement64 is < 16 or > int.MaxValue ||
            comparison.MemoryDisplacement64 % (ulong)(width / 8) != 0 ||
            comparison.HasLockPrefix || comparison.HasRepPrefix || comparison.HasRepnePrefix ||
            comparison.SegmentPrefix != NativeRegister.None)
            return false;
        offset = (int)comparison.MemoryDisplacement64;
        return true;
    }
}
