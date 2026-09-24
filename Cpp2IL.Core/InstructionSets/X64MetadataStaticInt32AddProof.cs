using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves one complete x64 body that adds an unchanged Int32 parameter to an Int32
/// static field of its own TypeInfo. The native initializer is authenticated and
/// the field read supplies the same class initialization in managed IL.
/// </summary>
internal static class X64MetadataStaticInt32AddProof
{
    private static readonly byte[] SavedRbxFrame = [0x06, 0x32, 0x02, 0x30];

    internal sealed record Evidence(FieldAnalysisContext Field, ulong TypeInfoSlot);
    internal sealed record Shape(ulong Flag, ulong TypeInfoSlot, ulong Initializer,
        ulong FieldOffset);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            !OrdinaryMethod(method) ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            !X64MetadataStaticGetterProof.OrdinaryOwner(owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            definition.InternalParameterData is not [var rawParameter] ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            method.Parameters is not [var parameter] ||
            !ReferenceEquals(parameter.Definition, rawParameter) ||
            !ReferenceEquals(parameter.DeclaringMethod, method) ||
            parameter.ParameterIndex != 0 || parameter.IsRef ||
            parameter.Attributes != parameter.DefaultAttributes ||
            parameter.OverrideParameterType != null ||
            rawParameter.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemInt32Type) ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemInt32Type) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            method.UnderlyingPointer is 0 or ulong.MaxValue ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings is not [var bound] || !ReferenceEquals(bound, method) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind)
            return null;

        var region = unwind.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
            region.End <= region.Start || region.End - region.Start is < 48 or > 96)
            return null;

        method.EnsureRawBytes();
        var native = X86Utils.Iterate(method).TakeWhile(instruction => instruction.IP < region.End).ToArray();
        var rawStart = pe.MapVirtualAddressToRaw(region.Start, false);
        var rawEnd = pe.MapVirtualAddressToRaw(region.End - 1, false);
        if (native.Length is < 15 or > 28 || rawStart < 0 || rawEnd < rawStart ||
            (ulong)(rawEnd - rawStart) != region.End - region.Start - 1 ||
            rawEnd >= pe.GetRawBinaryContent().Length || native[0].IP != region.Start ||
            native[14].NextIP > region.End ||
            native.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            native.Where((instruction, index) => index > 0 &&
                instruction.IP != native[index - 1].NextIP).Any() ||
            native.Skip(15).Any(instruction => instruction.Code != Code.Int3) ||
            !X64NativePaddingProof.HasInt3Padding(pe, native[14].NextIP, region.End) ||
            !unwind.MatchesUnwind(region.Start, region.End, 6, 0, SavedRbxFrame) ||
            TryProveShape(native.Take(15).ToArray()) is not { } shape ||
            X86CallerExceptionRegionProof.Check(method, native.Take(15).ToArray(),
                new HashSet<ulong>()) != null)
            return null;

        if (shape.TypeInfoSlot <= shape.Flag && shape.Flag - shape.TypeInfoSlot < 8 ||
            !X64MetadataStaticGetterProof.FileBackedWritableData(pe, unwind,
                shape.TypeInfoSlot, 8) ||
            !X64MetadataStaticGetterProof.ZeroInitializedWritableData(unwind, shape.Flag, 1) ||
            app.GetOrCreateKeyFunctionAddresses().il2cpp_codegen_initialize_runtime_metadata !=
                shape.Initializer ||
            !X64MetadataInitializationHelperProof.TryIdentify(app, pe, unwind, shape.Initializer))
            return null;

        var usage = app.LibCpp2IlContext.GetRawTypeGlobalByAddress(shape.TypeInfoSlot);
        if (usage is not { Type: MetadataUsageType.TypeInfo, IsValid: true } ||
            !ReferenceEquals(app.ResolveIl2CppType(usage.AsType()), owner) ||
            shape.FieldOffset > int.MaxValue ||
            shape.FieldOffset + 4 > owner.Definition.RawSizes.static_fields_size)
            return null;

        var fields = owner.Fields.Where(field => field.IsStatic &&
            field.Offset == (long)shape.FieldOffset).ToArray();
        if (fields is not [{ } matched] ||
            !X64MetadataStaticGetterProof.UnchangedField(matched, method.ReturnType,
                definition.RawReturnType, 4))
            return null;
        return new Evidence(matched, shape.TypeInfoSlot);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 15 || !PushOrPop(body[0], Mnemonic.Push) ||
            !Stack(body[1], Mnemonic.Sub) ||
            !RipByteComparison(body[2]) ||
            !RegisterCopy(body[3], NativeRegister.EBX, NativeRegister.ECX) ||
            body[4].Mnemonic != Mnemonic.Jne || body[4].Op0Kind != OpKind.NearBranch64 ||
            body[4].NearBranchTarget != body[8].IP ||
            !RipLea(body[5], NativeRegister.RCX) ||
            body[6].Code != Code.Call_rel32_64 || body[6].Op0Kind != OpKind.NearBranch64 ||
            body[6].NearBranchTarget == 0 ||
            !RipByteStoreOne(body[7]) ||
            body[7].IPRelativeMemoryAddress != body[2].IPRelativeMemoryAddress ||
            !RipPointerLoad(body[8], NativeRegister.RAX) ||
            body[8].IPRelativeMemoryAddress != body[5].IPRelativeMemoryAddress ||
            !PointerLoad(body[9], NativeRegister.RDX, NativeRegister.RAX,
                (ulong)Il2CppClassLayout.StaticFieldsOffset64, 8) ||
            !PointerLoad(body[10], NativeRegister.EAX, NativeRegister.RDX,
                body[10].MemoryDisplacement64, 4) ||
            body[10].MemoryDisplacement64 > int.MaxValue ||
            body[11].Code != Code.Add_r32_rm32 ||
            body[11].Op0Kind != OpKind.Register || body[11].Op0Register != NativeRegister.EAX ||
            body[11].Op1Kind != OpKind.Register || body[11].Op1Register != NativeRegister.EBX ||
            !Stack(body[12], Mnemonic.Add) || !PushOrPop(body[13], Mnemonic.Pop) ||
            body[14].Code != Code.Retnq || body[14].OpCount != 0)
            return null;
        return new Shape(body[2].IPRelativeMemoryAddress, body[5].IPRelativeMemoryAddress,
            body[6].NearBranchTarget, body[10].MemoryDisplacement64);
    }

    private static bool OrdinaryMethod(MethodAnalysisContext method) =>
        method.IsStatic && !method.IsVirtual && !method.IsVoid &&
        method.Name is not (".ctor" or ".cctor") && method.Name == method.DefaultName &&
        method.OverrideReturnType == null && method.Parameters.Count == 1 &&
        method.GenericParameters.Count == 0 &&
        method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                  MethodImplAttributes.ManagedMask |
                                  MethodImplAttributes.InternalCall)) == 0;

    private static bool PushOrPop(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RBX && instruction.OpCount == 1;

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x20;

    private static bool RegisterCopy(NativeInstruction instruction,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == Code.Mov_r32_rm32 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool RipByteComparison(NativeInstruction instruction) =>
        instruction.Code == Code.Cmp_rm8_imm8 && RipMemory(instruction, 0, 1) &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 0;

    private static bool RipByteStoreOne(NativeInstruction instruction) =>
        instruction.Code == Code.Mov_rm8_imm8 && RipMemory(instruction, 0, 1) &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 1;

    private static bool RipLea(NativeInstruction instruction, NativeRegister destination) =>
        instruction.Code == Code.Lea_r64_m && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && RipMemory(instruction, 1, 0);

    private static bool RipPointerLoad(NativeInstruction instruction, NativeRegister destination) =>
        instruction.Code == Code.Mov_r64_rm64 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && RipMemory(instruction, 1, 8);

    private static bool RipMemory(NativeInstruction instruction, int operand, int width) =>
        instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP && instruction.MemoryIndex == NativeRegister.None &&
        (width == 0 || instruction.MemorySize.GetSize() == width);

    private static bool PointerLoad(NativeInstruction instruction, NativeRegister destination,
        NativeRegister source, ulong offset, int width) =>
        instruction.Code == (width == 8 ? Code.Mov_r64_rm64 : Code.Mov_r32_rm32) &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == source &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 == offset && instruction.MemorySize.GetSize() == width;
}
