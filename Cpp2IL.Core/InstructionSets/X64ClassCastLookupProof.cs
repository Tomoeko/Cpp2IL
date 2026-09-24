using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
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
/// Authenticates a complete exact-target zero-argument class cast lookup. Both
/// observed register allocations implement the same unsigned depth test and
/// parent-table lookup; no other native body is admitted. The result is either
/// the original field reference or null, matching managed isinst.
/// </summary>
internal static class X64ClassCastLookupProof
{
    private static readonly byte[] SavedRbxFrame = [0x06, 0x32, 0x02, 0x30];

    internal sealed record Evidence(FieldAnalysisContext SourceField,
        TypeAnalysisContext TargetType);

    internal sealed record Shape(int FieldOffset, ulong Flag, ulong TypeInfoSlot,
        ulong Initializer);

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
                method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
                !PublicOrdinaryClass(owner) ||
                method.Definition is not { GenericContainer: null, parameterCount: 0,
                    RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
                !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
                (definition.InternalParameterData?.Length ?? 0) != 0 ||
                method.IsStatic || method.IsVirtual || method.IsVoid ||
                method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
                method.Parameters.Count != 0 || method.GenericParameters.Count != 0 ||
                method.OverrideReturnType != null ||
                method.Attributes != method.DefaultAttributes ||
                method.ImplAttributes != method.DefaultImplAttributes ||
                (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
                (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                          MethodImplAttributes.ManagedMask |
                                          MethodImplAttributes.InternalCall)) != 0 ||
                RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
                !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
                method.ReturnType is not { Definition: { GenericContainer: null } } target ||
                !PublicOrdinaryClass(target) ||
                !ReferenceEquals(target.DeclaringAssembly, owner.DeclaringAssembly) ||
                method.UnderlyingPointer == 0 || decoded.Count < 37 ||
                decoded[0].IP != method.UnderlyingPointer ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
                bindings is not [var bound] || !ReferenceEquals(bound, method))
                return null;

            var region = unwind.ClassifySpan(method.UnderlyingPointer,
                method.UnderlyingPointer + 1);
            if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
                region.Start != method.UnderlyingPointer || region.RootStart != region.Start ||
                region.End <= region.Start || region.End - region.Start is < 120 or > 160 ||
                unwind.ClassifySpan(region.Start, region.End).Kind !=
                X64UnwindProof.SpanKind.HandlerFree ||
                !unwind.MatchesUnwind(region.Start, region.End, 6, 0, SavedRbxFrame) ||
                !FileBacked(pe, region.Start, region.End))
                return null;

            var withinRegion = decoded.TakeWhile(instruction => instruction.IP < region.End).ToArray();
            var body = withinRegion.TakeWhile(instruction => instruction.Code != Code.Int3).ToArray();
            var shape = TryProveShape(body);
            if (shape == null || body[^1].NextIP > region.End ||
                region.End - body[^1].NextIP > 15 ||
                withinRegion.Skip(body.Length).Any(instruction => instruction.Code != Code.Int3) ||
                !X64NativePaddingProof.HasInt3Padding(pe, body[^1].NextIP, region.End) ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null ||
                Enumerable.Range(1, checked((int)(region.End - region.Start) - 1)).Any(offset =>
                    app.MethodsByAddress.ContainsKey(region.Start + (ulong)offset)))
                return null;

            method.EnsureRawBytes();
            var bodyLength = checked((int)(body[^1].NextIP - region.Start));
            var rawStart = pe.MapVirtualAddressToRaw(region.Start, false);
            if (method.RawBytes.Length < bodyLength || rawStart < 0 ||
                rawStart > pe.GetRawBinaryContent().Length - bodyLength ||
                !pe.GetRawBinaryContent().Slice((int)rawStart, bodyLength)
                    .SequenceEqual(method.RawBytes.AsSpan().Slice(0, bodyLength)) ||
                !X86Utils.Iterate(method).Take(body.Length).SequenceEqual(body))
                return null;

            return BindProvedShape(method, shape);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    // A separately testable binding step. Find must first authenticate the complete
    // native body and its exact PE bytes before this evidence can authorize emission.
    internal static Evidence? BindProvedShape(MethodAnalysisContext method, Shape shape)
    {
        try
        {
            var app = method.AppContext;
            if (app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not
                    { } unwind ||
                method.DeclaringType is not { } owner ||
                method.ReturnType is not { } target ||
                !PublicOrdinaryClass(owner) || !PublicOrdinaryClass(target) ||
                shape.TypeInfoSlot <= shape.Flag &&
                shape.Flag - shape.TypeInfoSlot < 8 ||
                !X64MetadataStaticGetterProof.FileBackedWritableData(pe, unwind,
                    shape.TypeInfoSlot, 8) ||
                !X64PeOnceFlagProof.IsInitiallyZero(pe, unwind, shape.Flag) ||
                shape.Initializer != app.GetOrCreateKeyFunctionAddresses()
                    .il2cpp_codegen_initialize_runtime_metadata ||
                !X64MetadataInitializationHelperProof.TryIdentify(app, pe, unwind,
                    shape.Initializer))
                return null;

            var usage = app.LibCpp2IlContext.GetRawTypeGlobalByAddress(shape.TypeInfoSlot);
            if (usage is not { Type: MetadataUsageType.TypeInfo, IsValid: true } ||
                !ReferenceEquals(app.ResolveIl2CppType(usage.AsType()), target))
                return null;

            var fields = owner.Fields.Where(field => !field.IsStatic &&
                field.Offset == shape.FieldOffset).ToArray();
            if (fields is not [{ } field] || field.Name != field.DefaultName ||
                field.BackingData?.Field.RawFieldType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } ||
                field.FieldType is not { Definition: { GenericContainer: null } } source ||
                !PublicOrdinaryClass(source) ||
                !SameOrDirectlyReferencedAssembly(owner.DeclaringAssembly,
                    source.DeclaringAssembly) ||
                !DerivesFrom(target, source) ||
                !NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(
                    new FieldReference(field,
                        new LocalVariable("proved-cast-owner",
                            new ISIL.Register(null, "rcx"), owner), shape.FieldOffset)))
                return null;

            return new Evidence(field, target);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    // Pure native predicate supports negative mutation tests without altered binaries.
    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count is not (37 or 38) || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any())
            return null;

        var targetVariant = body.Count == 38;
        var classRegister = targetVariant ? NativeRegister.R9 : NativeRegister.RAX;
        var depthRegister = targetVariant ? NativeRegister.AL : NativeRegister.CL;
        var hierarchyIndex = targetVariant ? 21 : 20;
        var successIndex = targetVariant ? 24 : 23;
        var failureIndex = targetVariant ? 31 : 30;
        if (!PushRbx(body[0]) || !Stack(body[1], Mnemonic.Sub) ||
            !RipCompareZero(body[2]) ||
            !Move(body[3], NativeRegister.RBX, NativeRegister.RCX) ||
            !Branch(body[4], Code.Jne_rel8_64, body[8].IP) ||
            !RipLea(body[5], NativeRegister.RCX) || !DirectCall(body[6]) ||
            !RipStoreOne(body[7], body[2].IPRelativeMemoryAddress) ||
            !ReferenceFieldLoad(body[8], out var fieldOffset) ||
            !SelfTest(body[9], NativeRegister.RDX) ||
            !Branch(body[10], Code.Jne_rel8_64, body[15].IP) ||
            !ZeroEax(body[11]) || !ReturnEpilog(body, 12) ||
            !RipLoad(body[15], NativeRegister.R8, body[5].IPRelativeMemoryAddress) ||
            !ObjectClassLoad(body[16], classRegister) ||
            !TargetDepthLoad(body[17], targetVariant ? NativeRegister.EAX : NativeRegister.ECX) ||
            !ClassDepthCompare(body[18], classRegister, depthRegister) ||
            !Branch(body[19], Code.Jb_rel8_64, body[failureIndex].IP) ||
            targetVariant && !SecondZeroExtend(body[20]) ||
            !HierarchyLoad(body[hierarchyIndex], classRegister) ||
            !HierarchyCompare(body[hierarchyIndex + 1]) ||
            !Branch(body[hierarchyIndex + 2], Code.Jne_rel8_64, body[failureIndex].IP) ||
            !SuccessReturn(body, successIndex) || !FailureReturn(body, failureIndex))
            return null;

        return new Shape(fieldOffset, body[2].IPRelativeMemoryAddress,
            body[5].IPRelativeMemoryAddress, body[6].NearBranchTarget);
    }

    internal static bool PublicOrdinaryClass(TypeAnalysisContext type) =>
        X64MetadataStaticGetterProof.OrdinaryOwner(type) &&
        type.Visibility == TypeAttributes.Public && type.DeclaringType == null;

    // A class initializer on an ancestor does not change the class hierarchy
    // tested by this exact native isinst body. Keep the cast target and the
    // field's declaring class on the stricter path: resolving target TypeInfo
    // can surface a cached initialization failure before the field read.
    internal static bool PublicCastAncestor(TypeAnalysisContext type)
    {
        if (PublicOrdinaryClass(type))
            return true;

        var constructors = type.Methods.Where(method => method.Name == ".cctor").ToArray();
        if (constructors is not [var initializer] ||
            type.IsValueType || type.IsInterface || type.IsGenericInstance ||
            type.GenericParameters.Count != 0 ||
            type.Definition is not
                { HasCctor: true, PackingSizeIsDefault: true,
                    ClassSizeIsDefault: true,
                    RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                        NumMods: 0, Byref: 0, Pinned: 0 } } ||
            type.Visibility != TypeAttributes.Public || type.DeclaringType != null ||
            type.Name != type.DefaultName || type.OverrideNamespace != null ||
            type.Attributes != type.DefaultAttributes ||
            !ReferenceEquals(type.BaseType, type.DefaultBaseType) ||
            (type.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout ||
            !ReferenceEquals(initializer.DeclaringType, type) ||
            !initializer.IsStatic || !initializer.IsVoid ||
            initializer.Parameters.Count != 0 || initializer.GenericParameters.Count != 0 ||
            initializer.Name != initializer.DefaultName ||
            initializer.Attributes != initializer.DefaultAttributes ||
            initializer.ImplAttributes != initializer.DefaultImplAttributes)
            return false;

        return true;
    }

    internal static bool SameOrDirectlyReferencedAssembly(AssemblyAnalysisContext owner,
        AssemblyAnalysisContext source)
    {
        if (ReferenceEquals(owner, source))
            return true;
        if (owner.Definition == null || source.Definition == null ||
            owner.Name != owner.DefaultName || source.Name != source.DefaultName ||
            owner.Version != owner.DefaultVersion || source.Version != source.DefaultVersion ||
            owner.HashAlgorithm != owner.DefaultHashAlgorithm ||
            source.HashAlgorithm != source.DefaultHashAlgorithm ||
            owner.Flags != owner.DefaultFlags || source.Flags != source.DefaultFlags ||
            (owner.Culture ?? "") != (owner.DefaultCulture ?? "") ||
            (source.Culture ?? "") != (source.DefaultCulture ?? "") ||
            !(owner.PublicKey ?? []).SequenceEqual(owner.DefaultPublicKey ?? []) ||
            !(source.PublicKey ?? []).SequenceEqual(source.DefaultPublicKey ?? []) ||
            !(owner.PublicKeyToken ?? []).SequenceEqual(owner.DefaultPublicKeyToken ?? []) ||
            !(source.PublicKeyToken ?? []).SequenceEqual(source.DefaultPublicKeyToken ?? []))
            return false;

        return owner.Definition.ReferencedAssemblies.Count(reference =>
            ReferenceEquals(reference, source.Definition)) == 1;
    }

    private static bool DerivesFrom(TypeAnalysisContext target, TypeAnalysisContext source)
    {
        var seen = new HashSet<TypeAnalysisContext>();
        var foundSource = false;
        for (var type = target; type != null && seen.Add(type); type = type.BaseType)
        {
            if (ReferenceEquals(type, target.AppContext.SystemTypes.SystemObjectType))
                return foundSource;
            if (!(ReferenceEquals(type, target) || ReferenceEquals(type, source)
                    ? PublicOrdinaryClass(type)
                    : PublicCastAncestor(type)))
                return false;
            if (ReferenceEquals(type, source))
                foundSource = true;
        }
        return false;
    }

    private static bool FileBacked(PE pe, ulong start, ulong end)
    {
        if (end <= start || end - start > int.MaxValue)
            return false;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        return first >= 0 && last >= first &&
               (ulong)(last - first) == end - start - 1 &&
               last < pe.GetRawBinaryContent().Length;
    }

    private static bool PushRbx(NativeInstruction instruction) =>
        instruction.Code == Code.Push_r64 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RBX;

    private static bool Stack(NativeInstruction instruction, Mnemonic mnemonic) =>
        instruction.Mnemonic == mnemonic && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind == OpKind.Immediate8to64 &&
        instruction.GetImmediate(1) == 0x20;

    private static bool Move(NativeInstruction instruction, NativeRegister destination,
        NativeRegister source) =>
        instruction.Code == Code.Mov_r64_rm64 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool RipCompareZero(NativeInstruction instruction) =>
        instruction.Code == Code.Cmp_rm8_imm8 && instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 1 &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 0;

    private static bool RipLea(NativeInstruction instruction, NativeRegister destination) =>
        instruction.Code == Code.Lea_r64_m && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None;

    private static bool RipStoreOne(NativeInstruction instruction, ulong flag) =>
        instruction.Code == Code.Mov_rm8_imm8 && instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 1 &&
        instruction.IPRelativeMemoryAddress == flag &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 1;

    private static bool DirectCall(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 &&
        instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget != 0;

    private static bool Branch(NativeInstruction instruction, Code code, ulong target) =>
        instruction.Code == code && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool ReferenceFieldLoad(NativeInstruction instruction, out int offset)
    {
        offset = 0;
        if (instruction.MemoryDisplacement64 > int.MaxValue)
            return false;
        offset = (int)instruction.MemoryDisplacement64;
        return instruction.Code == Code.Mov_r64_rm64 &&
               instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register == NativeRegister.RDX &&
               instruction.Op1Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RBX &&
               instruction.MemoryIndex == NativeRegister.None &&
               instruction.MemorySize.GetSize() == 8 && offset >= 16;
    }

    private static bool SelfTest(NativeInstruction instruction, NativeRegister register) =>
        instruction.Code == Code.Test_rm64_r64 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op0Register == register && instruction.Op1Register == register;

    private static bool RipLoad(NativeInstruction instruction, NativeRegister destination,
        ulong address) =>
        instruction.Code == Code.Mov_r64_rm64 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemorySize.GetSize() == 8 &&
        instruction.IPRelativeMemoryAddress == address;

    private static bool ObjectClassLoad(NativeInstruction instruction, NativeRegister destination) =>
        MemoryLoad(instruction, destination, NativeRegister.RDX, 0);

    private static bool HierarchyLoad(NativeInstruction instruction, NativeRegister classRegister) =>
        MemoryLoad(instruction, NativeRegister.RAX, classRegister,
            Il2CppClassLayout.TypeHierarchyOffset64);

    private static bool MemoryLoad(NativeInstruction instruction, NativeRegister destination,
        NativeRegister basis, ulong offset) =>
        instruction.Code == Code.Mov_r64_rm64 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == basis && instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 == offset &&
        instruction.MemorySize.GetSize() == 8;

    private static bool TargetDepthLoad(NativeInstruction instruction,
        NativeRegister destination) =>
        instruction.Code == Code.Movzx_r32_rm8 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.R8 &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 ==
            Il2CppClassLayout.TypeHierarchyDepthOffset64 &&
        instruction.MemorySize.GetSize() == 1;

    private static bool ClassDepthCompare(NativeInstruction instruction,
        NativeRegister classRegister, NativeRegister depthRegister) =>
        instruction.Code == Code.Cmp_rm8_r8 && instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == classRegister &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 ==
            Il2CppClassLayout.TypeHierarchyDepthOffset64 &&
        instruction.MemorySize.GetSize() == 1 &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == depthRegister;

    private static bool SecondZeroExtend(NativeInstruction instruction) =>
        instruction.Code == Code.Movzx_r32_rm8 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.ECX &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == NativeRegister.AL;

    private static bool HierarchyCompare(NativeInstruction instruction) =>
        instruction.Code == Code.Cmp_rm64_r64 && instruction.Op0Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RAX &&
        instruction.MemoryIndex == NativeRegister.RCX &&
        instruction.MemoryIndexScale == 8 &&
        instruction.MemoryDisplacement64 == unchecked((ulong)-8) &&
        instruction.MemorySize.GetSize() == 8 &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == NativeRegister.R8;

    private static bool ZeroEax(NativeInstruction instruction) =>
        instruction.Code == Code.Xor_r32_rm32 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.EAX &&
        instruction.Op1Register == NativeRegister.EAX;

    private static bool ReturnEpilog(IReadOnlyList<NativeInstruction> body, int start) =>
        Stack(body[start], Mnemonic.Add) &&
        body[start + 1].Code == Code.Pop_r64 &&
        body[start + 1].Op0Kind == OpKind.Register &&
        body[start + 1].Op0Register == NativeRegister.RBX &&
        body[start + 2].Code == Code.Retnq && body[start + 2].OpCount == 0;

    private static bool SuccessReturn(IReadOnlyList<NativeInstruction> body, int start) =>
        ZeroEax(body[start]) &&
        body[start + 1].Code == Code.Mov_r8_imm8 &&
        body[start + 1].Op0Kind == OpKind.Register &&
        body[start + 1].Op0Register == NativeRegister.CL &&
        body[start + 1].Op1Kind == OpKind.Immediate8 &&
        body[start + 1].Immediate8 == 1 &&
        TestCl(body[start + 2]) && CmovOriginal(body[start + 3]) &&
        ReturnEpilog(body, start + 4);

    private static bool FailureReturn(IReadOnlyList<NativeInstruction> body, int start) =>
        ZeroEax(body[start]) &&
        body[start + 1].Code == Code.Xor_r8_rm8 &&
        body[start + 1].Op0Kind == OpKind.Register &&
        body[start + 1].Op1Kind == OpKind.Register &&
        body[start + 1].Op0Register == NativeRegister.CL &&
        body[start + 1].Op1Register == NativeRegister.CL &&
        TestCl(body[start + 2]) && CmovOriginal(body[start + 3]) &&
        ReturnEpilog(body, start + 4);

    private static bool TestCl(NativeInstruction instruction) =>
        instruction.Code == Code.Test_rm8_r8 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.CL &&
        instruction.Op1Register == NativeRegister.CL;

    private static bool CmovOriginal(NativeInstruction instruction) =>
        instruction.Code == Code.Cmovne_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RAX &&
        instruction.Op1Register == NativeRegister.RDX;
}
