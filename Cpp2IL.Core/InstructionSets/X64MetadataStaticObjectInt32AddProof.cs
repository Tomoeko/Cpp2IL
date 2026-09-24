using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedFieldReference = Cpp2IL.Core.ISIL.FieldReference;
using ManagedLocalVariable = Cpp2IL.Core.ISIL.LocalVariable;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete x64 static Int32 addition with an Int32 field of one class
/// parameter. The metadata initializer runs before the exclusive native null arm;
/// the corresponding managed field access supplies the null check.
/// </summary>
internal static class X64MetadataStaticObjectInt32AddProof
{
    private static readonly byte[] SavedRbxFrame = [0x06, 0x32, 0x02, 0x30];

    internal sealed record Evidence(FieldAnalysisContext StaticField,
        FieldAnalysisContext InstanceField, ulong TypeInfoSlot);
    internal sealed record Shape(ulong Flag, ulong TypeInfoSlot, ulong Initializer,
        ulong StaticOffset, ulong InstanceOffset, ulong NullThrowTarget);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            !X64MetadataStaticInt32AddProof.OrdinaryMethod(method) ||
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
            rawParameter.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemInt32Type) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            method.UnderlyingPointer is 0 or ulong.MaxValue ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings is not [var bound] || !ReferenceEquals(bound, method) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind)
            return null;

        var box = parameter.ParameterType;
        if (!ReferenceEquals(box, app.ResolveIl2CppType(rawParameter.RawType)) ||
            box.Definition is not { GenericContainer: null,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 },
                PackingSizeIsDefault: true, ClassSizeIsDefault: true } ||
            box.IsValueType || box.IsInterface || box.IsGenericInstance ||
            box.GenericParameters.Count != 0 || box.Name != box.DefaultName ||
            box.OverrideNamespace != null || box.Attributes != box.DefaultAttributes ||
            !ReferenceEquals(box.BaseType, box.DefaultBaseType))
            return null;

        var region = unwind.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
            region.End <= region.Start || region.End - region.Start is < 64 or > 112)
            return null;

        method.EnsureRawBytes();
        var native = X86Utils.Iterate(method).TakeWhile(instruction => instruction.IP < region.End).ToArray();
        var rawStart = pe.MapVirtualAddressToRaw(region.Start, false);
        var rawEnd = pe.MapVirtualAddressToRaw(region.End - 1, false);
        if (native.Length is < 18 or > 32 || rawStart < 0 || rawEnd < rawStart ||
            (ulong)(rawEnd - rawStart) != region.End - region.Start - 1 ||
            rawEnd >= pe.GetRawBinaryContent().Length || native[0].IP != region.Start ||
            native[17].NextIP > region.End ||
            native.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            native.Where((instruction, index) => index > 0 &&
                instruction.IP != native[index - 1].NextIP).Any() ||
            native.Skip(18).Any(instruction => instruction.Code != Code.Int3) ||
            !X64NativePaddingProof.HasInt3Padding(pe, native[17].NextIP, region.End) ||
            !unwind.MatchesUnwind(region.Start, region.End, 6, 0, SavedRbxFrame) ||
            TryProveShape(native.Take(18).ToArray()) is not { } shape ||
            X86RuntimeNullThrowProof.TryIdentify(app, shape.NullThrowTarget) == null ||
            X86CallerExceptionRegionProof.Check(method, native.Take(18).ToArray(),
                new HashSet<ulong> { native[17].IP }) != null)
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
            shape.StaticOffset > int.MaxValue ||
            shape.StaticOffset + 4 > owner.Definition.RawSizes.static_fields_size ||
            shape.InstanceOffset > int.MaxValue ||
            shape.InstanceOffset + 4 > box.Definition.RawSizes.instance_size)
            return null;

        var staticFields = owner.Fields.Where(field => field.IsStatic &&
            field.Offset == (long)shape.StaticOffset).ToArray();
        if (staticFields is not [{ } staticField] ||
            !X64MetadataStaticGetterProof.UnchangedField(staticField, method.ReturnType,
                definition.RawReturnType, 4))
            return null;

        var instanceFields = box.Fields.Where(field => !field.IsStatic &&
            field.Offset == (long)shape.InstanceOffset).ToArray();
        if (instanceFields is not [{ } instanceField] ||
            instanceField.Name != instanceField.DefaultName ||
            !ReferenceEquals(instanceField.FieldType, app.SystemTypes.SystemInt32Type) ||
            instanceField.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4, NumMods: 0, Byref: 0, Pinned: 0 })
            return null;
        var receiver = new ManagedLocalVariable("native-receiver",
            new ManagedRegister(null, "rbx"), box);
        var access = new ManagedFieldReference(instanceField, receiver, (int)shape.InstanceOffset);
        if (!NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, 32))
            return null;
        return new Evidence(staticField, instanceField, shape.TypeInfoSlot);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 18 || !PushOrPop(body[0], Mnemonic.Push) ||
            !Stack(body[1], Mnemonic.Sub) || !RipByteComparison(body[2]) ||
            !RegisterCopy(body[3], NativeRegister.RBX, NativeRegister.RCX) ||
            body[4].Mnemonic != Mnemonic.Jne || body[4].Op0Kind != OpKind.NearBranch64 ||
            body[4].NearBranchTarget != body[8].IP ||
            !RipLea(body[5], NativeRegister.RCX) ||
            !DirectCall(body[6]) || !RipByteStoreOne(body[7]) ||
            body[7].IPRelativeMemoryAddress != body[2].IPRelativeMemoryAddress ||
            !TestRbx(body[8]) ||
            body[9].Mnemonic != Mnemonic.Je || body[9].Op0Kind != OpKind.NearBranch64 ||
            body[9].NearBranchTarget != body[17].IP ||
            !RipPointerLoad(body[10], NativeRegister.RAX) ||
            body[10].IPRelativeMemoryAddress != body[5].IPRelativeMemoryAddress ||
            !PointerLoad(body[11], NativeRegister.RDX, NativeRegister.RAX,
                (ulong)Il2CppClassLayout.StaticFieldsOffset64, 8) ||
            !PointerLoad(body[12], NativeRegister.EAX, NativeRegister.RDX,
                body[12].MemoryDisplacement64, 4) ||
            !AddInstanceField(body[13]) ||
            !Stack(body[14], Mnemonic.Add) ||
            !PushOrPop(body[15], Mnemonic.Pop) ||
            body[16].Code != Code.Retnq || body[16].OpCount != 0 ||
            !DirectCall(body[17]))
            return null;
        return new Shape(body[2].IPRelativeMemoryAddress,
            body[5].IPRelativeMemoryAddress, body[6].NearBranchTarget,
            body[12].MemoryDisplacement64, body[13].MemoryDisplacement64,
            body[17].NearBranchTarget);
    }

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
        instruction.Code == Code.Mov_r64_rm64 && instruction.Op0Kind == OpKind.Register &&
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
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        (width == 0 || instruction.MemorySize.GetSize() == width);

    private static bool PointerLoad(NativeInstruction instruction, NativeRegister destination,
        NativeRegister source, ulong offset, int width) =>
        instruction.Code == (width == 8 ? Code.Mov_r64_rm64 : Code.Mov_r32_rm32) &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == source &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 == offset && instruction.MemorySize.GetSize() == width;

    private static bool TestRbx(NativeInstruction instruction) =>
        instruction.Mnemonic == Mnemonic.Test && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RBX &&
        instruction.Op1Register == NativeRegister.RBX;

    private static bool AddInstanceField(NativeInstruction instruction) =>
        instruction.Code == Code.Add_r32_rm32 && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.EAX &&
        instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == NativeRegister.RBX &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 4;

    private static bool DirectCall(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;
}
