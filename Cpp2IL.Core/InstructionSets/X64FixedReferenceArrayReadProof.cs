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
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete fixed-index reference-array read with separate null and
/// bounds exits. The implicit managed array access replaces both exits.
/// </summary>
internal static class X64FixedReferenceArrayReadProof
{
    internal sealed record Evidence(FieldAnalysisContext ArrayField, int Index);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                method.DeclaringType is not { Definition: { GenericContainer: null,
                    RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
                !NullCheckedCall.IsReferenceClass(owner) ||
                owner.GenericParameters.Count != 0 ||
                owner.Name != owner.DefaultName ||
                owner.Namespace != owner.DefaultNamespace ||
                owner.Attributes != owner.DefaultAttributes ||
                !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
                owner.Definition is not { PackingSizeIsDefault: true,
                    ClassSizeIsDefault: true } ||
                !OrdinaryGetter(method, owner) ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                    out var bindings) ||
                bindings is not [var bound] || !ReferenceEquals(bound, method))
                return null;

            method.EnsureRawBytes();
            var start = method.UnderlyingPointer;
            var region = unwind.ClassifySpan(start, start + 1);
            if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
                region.Start != start || region.RootStart != start ||
                region.End <= start || region.End - start > 80)
                return null;
            var native = X86Utils.Iterate(method)
                .TakeWhile(instruction => instruction.IP < region.End).ToArray();
            var body = native.Take(12).ToArray();
            if (native.Length is < 12 or > 44 || body[0].IP != start ||
                body[^1].NextIP > region.End ||
                native.Skip(12).Any(instruction => instruction.Code != Code.Int3) ||
                body.Any(instruction => instruction.IsInvalid ||
                    instruction.CodeSize != CodeSize.Code64 ||
                    instruction.HasLockPrefix || instruction.HasRepPrefix ||
                    instruction.HasRepnePrefix ||
                    instruction.SegmentPrefix != NativeRegister.None) ||
                native.Where((instruction, index) => index > 0 &&
                    instruction.IP != native[index - 1].NextIP).Any() ||
                !FileBackedRegion(method, body, pe, unwind, region) ||
                !Stack(body[0], Mnemonic.Sub) || body[0].Length != 4 ||
                !ArrayLoad(body[1], out var fieldOffset) ||
                !Test(body[2]) ||
                body[3].Code != Code.Je_rel8_64 ||
                body[3].NearBranchTarget != body[9].IP ||
                !LengthCompare(body[4], pe, out var index) ||
                body[5].Code != Code.Jbe_rel8_64 ||
                body[5].NearBranchTarget != body[11].IP ||
                !ElementLoad(body[6], index, pe) ||
                !Stack(body[7], Mnemonic.Add) ||
                body[8].Code != Code.Retnq || body[8].OpCount != 0 ||
                body[9].Code != Code.Call_rel32_64 ||
                body[10].Code != Code.Int3 ||
                body[11].Code != Code.Call_rel32_64 ||
                X86RuntimeNullThrowProof.TryIdentify(app,
                    body[9].NearBranchTarget) == null ||
                !X86RuntimeBoundsThrowProof.TryIdentify(app,
                    body[11].NearBranchTarget) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { body[9].IP, body[11].IP }) != null)
                return null;

            var fields = owner.Fields.Where(field => !field.IsStatic &&
                field.Offset == (long)fieldOffset &&
                field.BackingData?.Field.RawFieldType is
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                        NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
            if (fields is not [{ } arrayField] ||
                arrayField.Name != arrayField.DefaultName ||
                arrayField.FieldType is not SzArrayTypeAnalysisContext
                    { ElementType: var element } ||
                !ReferenceEquals(element, method.ReturnType))
                return null;
            var ownerLocal = new LocalVariable("proved-owner",
                new ManagedRegister(null, "proved-owner"), owner);
            if (!NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                    new FieldReference(arrayField, ownerLocal, (int)fieldOffset)))
                return null;

            return new Evidence(arrayField, index);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool OrdinaryGetter(MethodAnalysisContext method,
        TypeAnalysisContext owner)
    {
        if (method.IsStatic || method.IsVirtual || method.IsVoid ||
            method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName ||
            method.GenericParameters.Count != 0 || method.Parameters.Count != 0 ||
            method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract |
                                  MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            method.UnderlyingPointer is 0 or ulong.MaxValue ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            method.ReturnType is not { Definition: { GenericContainer: null,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } } resultType ||
            resultType.IsValueType || resultType.IsInterface ||
            resultType.GenericParameters.Count != 0 ||
            resultType.Name != resultType.DefaultName ||
            resultType.Namespace != resultType.DefaultNamespace ||
            resultType.Attributes != resultType.DefaultAttributes)
            return false;

        var properties = owner.Properties.Where(property =>
            ReferenceEquals(property.Getter, method)).ToArray();
        if ((method.Attributes & MethodAttributes.SpecialName) == 0)
            return properties.Length == 0;
        return properties is [{ } property] && property.Setter == null &&
               property.Definition is { } rawProperty &&
               ReferenceEquals(rawProperty.Getter, definition) &&
               property.Name == property.DefaultName &&
               method.Name == "get_" + property.Name &&
               property.Attributes == property.DefaultAttributes &&
               property.OverridePropertyType == null && !property.IsStatic &&
               ReferenceEquals(property.PropertyType, resultType) &&
               rawProperty.RawPropertyType is
                   { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                       NumMods: 0, Byref: 0, Pinned: 0 };
    }

    private static bool FileBackedRegion(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> body, PE pe, X64UnwindProof.Index unwind,
        X64UnwindProof.SpanClassification region)
    {
        var start = method.UnderlyingPointer;
        var bodyEnd = body[^1].NextIP;
        if (region.End < bodyEnd || region.End - bodyEnd > 32 ||
            method.RawBytes.Length < (long)(bodyEnd - start) ||
            !unwind.MatchesUnwind(start, region.End, 4, 0,
                new byte[] { 4, 0x42 }) ||
            !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind,
                method.RawBytes.AsSpan().Slice(0,
                    checked((int)(bodyEnd - start))), start) ||
            method.AppContext.MethodsByAddress.Keys.Any(address =>
                address > start && address < region.End))
            return false;

        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(region.End - 1, false);
        var image = pe.GetRawBinaryContent();
        var length = checked((int)(region.End - start));
        if (first < 0 || first > image.Length - length ||
            last - first != length - 1)
            return false;
        for (var address = start; address < region.End; address++)
            if (!unwind.IsExecutableRva(checked((uint)(address - unwind.ImageBase))) ||
                pe.MapVirtualAddressToRaw(address, false) !=
                    first + (long)(address - start) ||
                (address >= bodyEnd &&
                 image[(int)(first + (long)(address - start))] != 0xCC))
                return false;
        return true;
    }

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
        instruction.GetImmediate(1) == 0x28;

    private static bool ArrayLoad(NativeInstruction instruction, out ulong offset)
    {
        offset = instruction.MemoryDisplacement64;
        return instruction.Code == Code.Mov_r64_rm64 &&
               instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register == NativeRegister.RAX &&
               instruction.Op1Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RCX &&
               instruction.MemoryIndex == NativeRegister.None &&
               instruction.MemorySize.GetSize() == 8 &&
               offset <= 0x1000 - 8;
    }

    private static bool Test(NativeInstruction instruction) =>
        instruction.Code == Code.Test_rm64_r64 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RAX &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == NativeRegister.RAX;

    private static bool LengthCompare(NativeInstruction instruction, PE pe,
        out int index)
    {
        index = 0;
        if (instruction.Code != Code.Cmp_rm32_imm8 ||
            instruction.Op0Kind != OpKind.Memory ||
            instruction.MemoryBase != NativeRegister.RAX ||
            instruction.MemoryIndex != NativeRegister.None ||
            instruction.MemoryDisplacement64 > uint.MaxValue ||
            !Il2CppArrayUtils.IsIl2cppLengthAccessor(
                (uint)instruction.MemoryDisplacement64, pe) ||
            instruction.MemorySize.GetSize() != 4 ||
            instruction.Op1Kind != OpKind.Immediate8to32 ||
            instruction.GetImmediate(1) > 127)
            return false;
        index = (int)instruction.GetImmediate(1);
        return true;
    }

    private static bool ElementLoad(NativeInstruction instruction, int index, PE pe) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RAX &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RAX &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 == Il2CppArrayUtils.GetFirstItemOffset(pe) +
            (ulong)(pe.PointerSizeBytes * index) &&
        instruction.MemorySize.GetSize() == pe.PointerSizeBytes;
}
