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
/// Proves a complete direct base effect, reference producer and guarded Boolean
/// call. The first call retains declared dispatch even when its method is virtual.
/// Folded tails require one applicable original declaration and an independent
/// native Boolean setter body; unrelated folded names never select a call target.
/// </summary>
internal static class X64BaseEffectBooleanTailProof
{
    internal sealed record Evidence(MethodAnalysisContext Effect, MethodAnalysisContext Producer,
        MethodAnalysisContext Target, bool Value, FieldAnalysisContext? FoldedSetterField);

    internal sealed record Shape(ulong Effect, ulong Producer, ulong Target,
        ulong NullCallsite, ulong NullHelper, bool Value);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
                !OrdinaryMethod(method, unique: false) || !VoidSignature(method) ||
                !Registers(method, "rcx", "rdx") ||
                method.DeclaringType is not { } owner ||
                ReadBody(method) is not { } body || TryProveShape(body) is not { } shape ||
                !app.MethodsByAddress.TryGetValue(shape.Effect, out var effects) ||
                effects is not [var effect] || effect.UnderlyingPointer != shape.Effect ||
                !ReferenceEquals(effect.AppContext, app) || !OrdinaryMethod(effect, unique: true) ||
                !VoidSignature(effect) || !Registers(effect, "rcx", "rdx") ||
                !AccessibleThisTarget(owner, effect) ||
                // A virtual direct call on this must name an original ancestor.
                effect.IsVirtual && ReferenceEquals(effect.DeclaringType, owner) ||
                !app.MethodsByAddress.TryGetValue(shape.Producer, out var producers) ||
                producers is not [var producer] || producer.UnderlyingPointer != shape.Producer ||
                !ReferenceEquals(producer.AppContext, app) || !OrdinaryMethod(producer, unique: true) ||
                producer.IsVirtual || producer.Parameters.Count != 0 ||
                producer.Definition?.RawReturnType?.Type != Il2CppTypeEnum.IL2CPP_TYPE_CLASS ||
                !OrdinaryClass(producer.ReturnType) || !Registers(producer, "rcx", "rdx") ||
                !AccessibleThisTarget(owner, producer) ||
                !app.MethodsByAddress.TryGetValue(shape.Target, out var targets) ||
                !TryBindTail(method, producer.ReturnType, shape.Target, targets, pe, unwind,
                    out var target, out var setterField) ||
                X86RuntimeNullThrowProof.TryIdentify(app, shape.NullHelper) == null ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { shape.NullCallsite }) != null)
                return null;

            return new Evidence(effect, producer, target, shape.Value, setterField);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool OrdinaryMethod(MethodAnalysisContext method, bool unique) =>
        !method.IsStatic && method.Name is not (".ctor" or ".cctor") &&
        method.Name == method.DefaultName && method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
            MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall |
            MethodImplAttributes.Synchronized)) == 0 &&
        method.OverrideReturnType == null && ReferenceEquals(method.ReturnType, method.DefaultReturnType) &&
        method.GenericParameters.Count == 0 &&
        method.Definition is { GenericContainer: null,
            RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0 } } &&
        method.DeclaringType is { } owner && OrdinaryClass(owner) &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, unique) &&
        method.AppContext.MethodsByAddress[method.UnderlyingPointer]
            .Count(candidate => ReferenceEquals(candidate, method)) == 1 &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method);

    private static bool VoidSignature(MethodAnalysisContext method) => method.IsVoid &&
        method.Parameters.Count == 0 &&
        method.Definition?.RawReturnType?.Type == Il2CppTypeEnum.IL2CPP_TYPE_VOID &&
        ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemVoidType);

    private static bool OrdinaryClass(TypeAnalysisContext type) =>
        NullCheckedCall.IsReferenceClass(type) &&
        type.Name == type.DefaultName && type.Namespace == type.DefaultNamespace &&
        type.Definition is { RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS or
            Il2CppTypeEnum.IL2CPP_TYPE_OBJECT, NumMods: 0, Byref: 0, Pinned: 0 } };

    private static bool OriginalAncestors(TypeAnalysisContext receiver,
        out HashSet<TypeAnalysisContext> ancestors)
    {
        ancestors = [];
        for (var current = receiver; current != null; current = current.BaseType)
            if (!ancestors.Add(current) || !OrdinaryClass(current) ||
                current.BaseType != null && current.Definition?.RawBaseType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT,
                        NumMods: 0, Byref: 0, Pinned: 0 })
                return false;
        return true;
    }

    private static bool AccessibleThisTarget(TypeAnalysisContext caller, MethodAnalysisContext target)
    {
        if (target.DeclaringType is not { } owner ||
            !OriginalAncestors(caller, out var ancestors) || !ancestors.Contains(owner) ||
            !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(
                caller.DeclaringAssembly, owner.DeclaringAssembly))
            return false;
        if (X64GuardedEnumParameterCallProof.AccessibleTarget(caller, target))
            return true;
        var access = target.Visibility;
        // Protected access is used only on the current instance. A protected
        // member on an unrelated receiver has a different accessibility domain.
        return PublicBaseTypeChain(caller, owner) &&
               (access is MethodAttributes.Family or MethodAttributes.FamORAssem ||
                access == MethodAttributes.FamANDAssem &&
                ReferenceEquals(caller.DeclaringAssembly, owner.DeclaringAssembly));
    }

    private static bool PublicBaseTypeChain(TypeAnalysisContext caller, TypeAnalysisContext type)
    {
        var sameAssembly = ReferenceEquals(caller.DeclaringAssembly, type.DeclaringAssembly);
        var seen = new HashSet<TypeAnalysisContext>();
        for (var current = type; current != null; current = current.DeclaringType)
        {
            if (!seen.Add(current) || !OrdinaryClass(current))
                return false;
            var visibility = current.Visibility;
            if (current.DeclaringType == null
                    ? visibility != TypeAttributes.Public && !(sameAssembly && visibility == TypeAttributes.NotPublic)
                    : visibility != TypeAttributes.NestedPublic && !(sameAssembly && visibility is
                        TypeAttributes.NestedAssembly or TypeAttributes.NestedFamORAssem))
                return false;
        }
        return true;
    }

    private static bool TryBindTail(MethodAnalysisContext caller, TypeAnalysisContext receiver, ulong address,
        IReadOnlyList<MethodAnalysisContext> bindings, PE pe, X64UnwindProof.Index unwind,
        out MethodAnalysisContext target, out FieldAnalysisContext? setterField)
    {
        target = null!;
        setterField = null;
        if (!OriginalAncestors(receiver, out var ancestors) ||
            bindings.Where(candidate => candidate.DeclaringType != null &&
                ancestors.Contains(candidate.DeclaringType)).ToArray() is not [var applicable] ||
            applicable.UnderlyingPointer != address || !ReferenceEquals(applicable.AppContext, caller.AppContext) ||
            !OrdinaryMethod(applicable, unique: false) || applicable.IsVirtual || !applicable.IsVoid ||
            applicable.Definition?.RawReturnType?.Type != Il2CppTypeEnum.IL2CPP_TYPE_VOID ||
            !ReferenceEquals(applicable.ReturnType, caller.AppContext.SystemTypes.SystemVoidType) ||
            applicable.Parameters is not [var parameter] ||
            applicable.Definition.InternalParameterData is not [var rawParameter] ||
            !ReferenceEquals(parameter.Definition, rawParameter) ||
            !ReferenceEquals(parameter.DeclaringMethod, applicable) || parameter.ParameterIndex != 0 ||
            parameter.Name != parameter.DefaultName || parameter.Attributes != parameter.DefaultAttributes ||
            parameter.OverrideParameterType != null || parameter.UseOverrideDefaultValue || parameter.IsRef ||
            !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) ||
            !ReferenceEquals(parameter.ParameterType, caller.AppContext.SystemTypes.SystemBooleanType) ||
            rawParameter.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !Registers(applicable, "rcx", "rdx", "r8") ||
            !X64GuardedEnumParameterCallProof.AccessibleTarget(caller.DeclaringType!, applicable) ||
            !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(
                caller.DeclaringType!.DeclaringAssembly, applicable.DeclaringType!.DeclaringAssembly))
            return false;

        if (bindings.Count > 1)
        {
            setterField = ProveFoldedSetter(applicable, pe, unwind);
            if (setterField == null)
                return false;
        }
        target = applicable;
        return true;
    }

    private static bool Registers(MethodAnalysisContext method, params string[] names)
    {
        if (method.AppContext.InstructionSet.CallingConventionResolver is not
                X64CallingConventionResolver resolver || resolver.ReturnsViaHiddenBuffer(method))
            return false;
        var operands = resolver.ResolveForParameters(method);
        return operands.Length == names.Length && operands.Select((operand, index) =>
            operand is ManagedRegister register && register == new ManagedRegister(null, names[index]))
            .All(valid => valid);
    }

    internal static NativeInstruction[]? ReadBody(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary is not PE pe ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } unwind ||
            method.UnderlyingPointer is 0 or ulong.MaxValue)
            return null;
        if (method.RawBytes.Length == 0)
            method.EnsureRawBytes();
        var start = method.UnderlyingPointer;
        var region = unwind.ClassifySpan(start, start + 1);
        if (region is not { Kind: X64UnwindProof.SpanKind.HandlerFree } ||
            region.Start != start || region.RootStart != start || region.End <= start ||
            region.End - start > 80 ||
            !unwind.MatchesUnwind(start, region.End, 6, 0, [6, 0x32, 2, 0x30]) ||
            !unwind.IsUnaffectedByBaseRelocation(start, checked((uint)(region.End - start))) ||
            method.AppContext.MethodsByAddress.Keys.Any(address => address > start && address < region.End))
            return null;
        var decoded = X86Utils.Iterate(method).TakeWhile(instruction => instruction.IP < region.End).ToArray();
        if (decoded.Length < 17 || decoded.Skip(17).Any(instruction => instruction.Code != Code.Int3))
            return null;
        var body = decoded.Take(17).ToArray();
        var bodyLength = checked((int)(body[^1].NextIP - start));
        if (body[0].IP != start || bodyLength > method.RawBytes.Length ||
            !ValidInstructions(body) || body[^1].NextIP > region.End || region.End - body[^1].NextIP > 16 ||
            !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind,
                method.RawBytes.AsSpan().Slice(0, bodyLength), start) ||
            !X64NativePaddingProof.HasInt3Padding(pe, body[^1].NextIP, region.End))
            return null;
        return body;
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 17 || !ValidInstructions(body) ||
            !SingleRegister(body[0], Code.Push_r64, NativeRegister.RBX) ||
            !Stack(body[1], Mnemonic.Sub) ||
            !(Pair(body[2], Code.Xor_r32_rm32, NativeRegister.EDX, NativeRegister.EDX) &&
                Pair(body[3], Code.Mov_r64_rm64, NativeRegister.RBX, NativeRegister.RCX) ||
              Pair(body[3], Code.Xor_r32_rm32, NativeRegister.EDX, NativeRegister.EDX) &&
                Pair(body[2], Code.Mov_r64_rm64, NativeRegister.RBX, NativeRegister.RCX)) ||
            !Transfer(body[4], Code.Call_rel32_64) ||
            !Pair(body[5], Code.Xor_r32_rm32, NativeRegister.EDX, NativeRegister.EDX) ||
            !Pair(body[6], Code.Mov_r64_rm64, NativeRegister.RCX, NativeRegister.RBX) ||
            !Transfer(body[7], Code.Call_rel32_64) ||
            !Pair(body[8], Code.Test_rm64_r64, NativeRegister.RAX, NativeRegister.RAX) ||
            body[9].Code != Code.Je_rel8_64 || body[9].Op0Kind != OpKind.NearBranch64 ||
            body[9].NearBranchTarget != body[16].IP ||
            !Pair(body[10], Code.Xor_r32_rm32, NativeRegister.R8D, NativeRegister.R8D) ||
            !(Pair(body[11], Code.Xor_r32_rm32, NativeRegister.EDX, NativeRegister.EDX) ||
              body[11].Code == Code.Mov_r8_imm8 && body[11].Op0Kind == OpKind.Register &&
              body[11].Op0Register == NativeRegister.DL && body[11].Op1Kind == OpKind.Immediate8 &&
              body[11].Immediate8 == 1) ||
            !Pair(body[12], Code.Mov_r64_rm64, NativeRegister.RCX, NativeRegister.RAX) ||
            !Stack(body[13], Mnemonic.Add) ||
            !SingleRegister(body[14], Code.Pop_r64, NativeRegister.RBX) ||
            !Transfer(body[15], Code.Jmp_rel32_64) || !Transfer(body[16], Code.Call_rel32_64))
            return null;
        return new Shape(body[4].NearBranchTarget, body[7].NearBranchTarget,
            body[15].NearBranchTarget, body[16].IP, body[16].NearBranchTarget,
            body[11].Code == Code.Mov_r8_imm8);
    }

    private static FieldAnalysisContext? ProveFoldedSetter(MethodAnalysisContext target,
        PE pe, X64UnwindProof.Index unwind)
    {
        if (target.RawBytes.Length == 0)
            target.EnsureRawBytes();
        var start = target.UnderlyingPointer;
        var body = X86Utils.Iterate(target).Take(2).ToArray();
        if (body.Length != 2 || !ValidInstructions(body) || body[0].IP != start ||
            body[0].Code != Code.Mov_rm8_r8 || body[0].Op0Kind != OpKind.Memory ||
            body[0].MemoryBase != NativeRegister.RCX || body[0].MemoryIndex != NativeRegister.None ||
            body[0].MemoryDisplacement64 is < 16 or > int.MaxValue ||
            body[0].MemorySize.GetSize() != 1 || body[0].Op1Kind != OpKind.Register ||
            body[0].Op1Register != NativeRegister.DL || body[1].Code != Code.Retnq ||
            body[1].OpCount != 0 || body[^1].NextIP <= start ||
            unwind.ClassifySpan(start, body[^1].NextIP).Kind != X64UnwindProof.SpanKind.NoEntry ||
            !unwind.IsUnaffectedByBaseRelocation(start, checked((uint)(body[^1].NextIP - start))) ||
            !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind,
                target.RawBytes.AsSpan().Slice(0, checked((int)(body[^1].NextIP - start))), start) ||
            target.AppContext.MethodsByAddress.Keys.Any(address => address > start && address < body[^1].NextIP) ||
            X86CallerExceptionRegionProof.Check(target, body, new HashSet<ulong>()) != null)
            return null;
        var offset = checked((int)body[0].MemoryDisplacement64);
        var fields = target.DeclaringType!.Fields.Where(field => !field.IsStatic && field.Offset == offset).ToArray();
        if (fields is not [var field] || field.Name != field.DefaultName ||
            (field.Attributes & (FieldAttributes.InitOnly | FieldAttributes.Literal)) != 0 ||
            !ReferenceEquals(field.FieldType, target.AppContext.SystemTypes.SystemBooleanType) ||
            field.BackingData?.Field.RawFieldType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            !NarrowFieldEqualityProof.HasUnchangedFieldLayout(new FieldReference(field,
                new LocalVariable("proved-setter-receiver", new ManagedRegister(null, "rcx"),
                    target.DeclaringType), offset), 8))
            return null;
        return field;
    }

    private static bool ValidInstructions(IReadOnlyList<NativeInstruction> body) =>
        !body.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
            instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
            instruction.SegmentPrefix != NativeRegister.None) &&
        !body.Where((instruction, index) => index > 0 && instruction.IP != body[index - 1].NextIP).Any();

    private static bool Stack(NativeInstruction instruction, Mnemonic operation) =>
        instruction.Mnemonic == operation && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind == OpKind.Immediate8to64 && instruction.GetImmediate(1) == 0x20;

    private static bool Pair(NativeInstruction instruction, Code code,
        NativeRegister first, NativeRegister second) => instruction.Code == code && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == first &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == second;

    private static bool SingleRegister(NativeInstruction instruction, Code code, NativeRegister register) =>
        instruction.Code == code && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register;

    private static bool Transfer(NativeInstruction instruction, Code code) => instruction.Code == code &&
        instruction.Length == 5 && instruction.OpCount == 1 && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;
}
