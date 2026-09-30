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
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds two observable reference-producing calls separated by a guarded Boolean
/// predicate. The selected branch repeats the producer before checking the new
/// receiver and calling the original typed literal tail.
/// </summary>
internal static class X64ConditionalCallResultTailProof
{
    internal sealed record Evidence(MethodAnalysisContext Producer, MethodAnalysisContext Predicate,
        MethodAnalysisContext Target, bool CallsWhenTrue, bool LiteralValue);

    internal sealed record Shape(ulong Producer, ulong Predicate, ulong Target,
        ulong NullCallsite, ulong NullHelper, bool CallsWhenTrue, bool LiteralValue);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || !CandidateShape(method) ||
                !OrdinaryMethod(method, unique: false) || !VoidSignature(method) ||
                method.Parameters.Count != 0 || !Registers(method, "rcx", "rdx") ||
                method.DeclaringType is not { } owner ||
                ReadBody(method) is not { } body || TryProveShape(body) is not { } shape ||
                Bind(app, shape.Producer) is not { } producer ||
                !OrdinaryMethod(producer, unique: true) || producer.IsVirtual ||
                producer.Parameters.Count != 0 ||
                producer.Definition?.RawReturnType?.Type != Il2CppTypeEnum.IL2CPP_TYPE_CLASS ||
                !OrdinaryClass(producer.ReturnType) || !Registers(producer, "rcx", "rdx") ||
                !AccessibleThisTarget(owner, producer) ||
                Bind(app, shape.Predicate) is not { } predicate ||
                !OrdinaryMethod(predicate, unique: true) || predicate.IsVirtual ||
                predicate.Parameters.Count != 0 ||
                predicate.Definition?.RawReturnType?.Type != Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN ||
                !ReferenceEquals(predicate.ReturnType, app.SystemTypes.SystemBooleanType) ||
                !Registers(predicate, "rcx", "rdx") ||
                !AccessibleReceiverTarget(owner, producer.ReturnType, predicate) ||
                Bind(app, shape.Target) is not { } target ||
                !OrdinaryMethod(target, unique: true) || target.IsVirtual || !VoidSignature(target) ||
                !LiteralParameter(target, shape.LiteralValue) || !Registers(target, "rcx", "rdx", "r8") ||
                !AccessibleReceiverTarget(owner, producer.ReturnType, target) ||
                X86RuntimeNullThrowProof.TryIdentify(app, shape.NullHelper) == null ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { shape.NullCallsite }) != null)
                return null;
            return new Evidence(producer, predicate, target, shape.CallsWhenTrue, shape.LiteralValue);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static MethodAnalysisContext? Bind(ApplicationAnalysisContext app, ulong address) =>
        app.MethodsByAddress.TryGetValue(address, out var methods) && methods is [var method] &&
        method.UnderlyingPointer == address && ReferenceEquals(method.AppContext, app) ? method : null;

    private static bool CandidateShape(MethodAnalysisContext method)
    {
        if (method.UnderlyingPointer is 0 or ulong.MaxValue)
            return false;
        if (method.RawBytes.Length == 0)
            method.EnsureRawBytes();
        var bytes = method.RawBytes.AsSpan()[..Math.Min(method.RawBytes.Length, 79)];
        // An inexpensive rejection only. Admission still requires complete PE,
        // unwind, cached-prefix, relocation, helper and managed binding evidence.
        return TryProveShape(X86Utils.Iterate(bytes, method.UnderlyingPointer, false).Take(27).ToArray()) != null;
    }

    private static bool OrdinaryMethod(MethodAnalysisContext method, bool unique) =>
        !method.IsStatic && method.Name is not (".ctor" or ".cctor") &&
        method.Name == method.DefaultName && method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
            MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) == 0 &&
        method.OverrideReturnType == null && ReferenceEquals(method.ReturnType, method.DefaultReturnType) &&
        method.GenericParameters.Count == 0 &&
        method.Definition is { GenericContainer: null, RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0 } } &&
        method.DeclaringType is { } owner && OrdinaryClass(owner) &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, unique) &&
        method.AppContext.MethodsByAddress[method.UnderlyingPointer]
            .Count(candidate => ReferenceEquals(candidate, method)) == 1 &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method);

    private static bool VoidSignature(MethodAnalysisContext method) => method.IsVoid &&
        method.Definition?.RawReturnType?.Type == Il2CppTypeEnum.IL2CPP_TYPE_VOID &&
        ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemVoidType);

    private static bool OrdinaryClass(TypeAnalysisContext type) =>
        NullCheckedCall.IsReferenceClass(type) &&
        type.Name == type.DefaultName && type.Namespace == type.DefaultNamespace &&
        type.Definition is { RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT,
            NumMods: 0, Byref: 0, Pinned: 0 } };

    private static bool HasOriginalAncestor(TypeAnalysisContext receiver, TypeAnalysisContext owner)
    {
        var found = false;
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = receiver; current != null; current = current.BaseType)
        {
            if (!visited.Add(current) || !OrdinaryClass(current) ||
                current.BaseType != null && current.Definition?.RawBaseType is not
                    { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT,
                        NumMods: 0, Byref: 0, Pinned: 0 })
                return false;
            found |= ReferenceEquals(current, owner);
        }
        return found;
    }

    private static bool AccessibleReceiverTarget(TypeAnalysisContext caller,
        TypeAnalysisContext receiver, MethodAnalysisContext target) =>
        target.DeclaringType is { } owner && HasOriginalAncestor(receiver, owner) &&
        X64GuardedEnumParameterCallProof.AccessibleTarget(caller, target) &&
        X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(caller.DeclaringAssembly, owner.DeclaringAssembly);

    private static bool AccessibleThisTarget(TypeAnalysisContext caller, MethodAnalysisContext target)
    {
        if (target.DeclaringType is not { } owner || !HasOriginalAncestor(caller, owner) ||
            !X64ClassCastLookupProof.SameOrDirectlyReferencedAssembly(caller.DeclaringAssembly, owner.DeclaringAssembly))
            return false;
        if (X64GuardedEnumParameterCallProof.AccessibleTarget(caller, target))
            return true;
        var sameAssembly = ReferenceEquals(caller.DeclaringAssembly, owner.DeclaringAssembly);
        var visited = new HashSet<TypeAnalysisContext>();
        for (var current = owner; current != null; current = current.DeclaringType)
        {
            if (!visited.Add(current) || !OrdinaryClass(current))
                return false;
            var access = current.Visibility;
            if (current.DeclaringType == null
                    ? access != TypeAttributes.Public && !(sameAssembly && access == TypeAttributes.NotPublic)
                    : access != TypeAttributes.NestedPublic && !(sameAssembly && access is
                        TypeAttributes.NestedAssembly or TypeAttributes.NestedFamORAssem))
                return false;
        }
        // This protected call uses the current instance along an original ancestor
        // path. A protected member on another receiver has a different domain.
        return target.Visibility is MethodAttributes.Family or MethodAttributes.FamORAssem ||
               sameAssembly && target.Visibility == MethodAttributes.FamANDAssem;
    }

    private static bool LiteralParameter(MethodAnalysisContext target, bool value)
    {
        if (target.Parameters is not [var parameter] ||
            target.Definition?.InternalParameterData is not [var definition] ||
            !ReferenceEquals(parameter.Definition, definition) ||
            !ReferenceEquals(parameter.DeclaringMethod, target) || parameter.ParameterIndex != 0 ||
            parameter.Name != parameter.DefaultName || parameter.Attributes != parameter.DefaultAttributes ||
            parameter.IsRef || parameter.OverrideParameterType != null || parameter.UseOverrideDefaultValue ||
            !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) ||
            definition.RawType is not { NumMods: 0, Byref: 0, Pinned: 0 } raw)
            return false;
        return raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN &&
                   ReferenceEquals(parameter.ParameterType, target.AppContext.SystemTypes.SystemBooleanType) ||
               !value && raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_I4 &&
                   ReferenceEquals(parameter.ParameterType, target.AppContext.SystemTypes.SystemInt32Type);
    }

    private static bool Registers(MethodAnalysisContext method, params string[] names)
    {
        if (method.AppContext.InstructionSet.CallingConventionResolver is not X64CallingConventionResolver resolver ||
            resolver.ReturnsViaHiddenBuffer(method))
            return false;
        var arguments = resolver.ResolveForParameters(method);
        return arguments.Length == names.Length && arguments.Select((argument, index) =>
            argument is ManagedRegister register && register == new ManagedRegister(null, names[index])).All(valid => valid);
    }

    internal static NativeInstruction[]? ReadBody(MethodAnalysisContext method)
    {
        if (method.RawBytes.Length == 0)
            method.EnsureRawBytes();
        if (method.UnderlyingPointer is 0 or ulong.MaxValue ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } unwind)
            return null;
        var start = method.UnderlyingPointer;
        var region = unwind.ClassifySpan(start, start + 1);
        if (region is not { Kind: X64UnwindProof.SpanKind.HandlerFree } || region.Start != start ||
            region.RootStart != start || region.End - start is < 79 or > 96 ||
            !unwind.MatchesUnwind(start, region.End, 6, 0, [6, 0x32, 2, 0x30]) ||
            X64NativeInstructionReader.ReadRootBody(method) is not { } body || body.Length < 27 ||
            body.Skip(27).Any(instruction => instruction.Code != Code.Int3))
            return null;
        return body.Take(27).ToArray();
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 27 || body[0].IP > ulong.MaxValue - 79 || body[^1].NextIP != body[0].IP + 79 ||
            body.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 && instruction.IP != body[index - 1].NextIP).Any() ||
            !SingleRegister(body[0], Code.Push_r64, NativeRegister.RBX) || body[0].Length != 2 ||
            !SavedStack(body[1], Mnemonic.Sub) ||
            !Registers(body[2], Code.Xor_r32_rm32, NativeRegister.EDX, NativeRegister.EDX) ||
            !Registers(body[3], Code.Mov_r64_rm64, NativeRegister.RBX, NativeRegister.RCX) ||
            !Transfer(body[4], Code.Call_rel32_64) ||
            !Registers(body[5], Code.Test_rm64_r64, NativeRegister.RAX, NativeRegister.RAX) ||
            !Branch(body[6], Code.Je_rel8_64, body[26].IP) ||
            !Registers(body[7], Code.Xor_r32_rm32, NativeRegister.EDX, NativeRegister.EDX) ||
            !Registers(body[8], Code.Mov_r64_rm64, NativeRegister.RCX, NativeRegister.RAX) ||
            !Transfer(body[9], Code.Call_rel32_64) ||
            !Registers(body[10], Code.Test_rm8_r8, NativeRegister.AL, NativeRegister.AL) ||
            !(Branch(body[11], Code.Je_rel8_64, body[23].IP) ||
              Branch(body[11], Code.Jne_rel8_64, body[23].IP)) ||
            !Registers(body[12], Code.Xor_r32_rm32, NativeRegister.EDX, NativeRegister.EDX) ||
            !Registers(body[13], Code.Mov_r64_rm64, NativeRegister.RCX, NativeRegister.RBX) ||
            !Transfer(body[14], Code.Call_rel32_64) || body[14].NearBranchTarget != body[4].NearBranchTarget ||
            !Registers(body[15], Code.Test_rm64_r64, NativeRegister.RAX, NativeRegister.RAX) ||
            !Branch(body[16], Code.Je_rel8_64, body[26].IP) ||
            !Registers(body[17], Code.Xor_r32_rm32, NativeRegister.R8D, NativeRegister.R8D) ||
            !BooleanLiteral(body[18]) ||
            !Registers(body[19], Code.Mov_r64_rm64, NativeRegister.RCX, NativeRegister.RAX) ||
            !SavedStack(body[20], Mnemonic.Add) || !SingleRegister(body[21], Code.Pop_r64, NativeRegister.RBX) ||
            !Transfer(body[22], Code.Jmp_rel32_64) || !SavedStack(body[23], Mnemonic.Add) ||
            !SingleRegister(body[24], Code.Pop_r64, NativeRegister.RBX) ||
            body[25].Code != Code.Retnq || body[25].OpCount != 0 ||
            !Transfer(body[26], Code.Call_rel32_64))
            return null;
        return new Shape(body[4].NearBranchTarget, body[9].NearBranchTarget, body[22].NearBranchTarget,
            body[26].IP, body[26].NearBranchTarget, body[11].Code == Code.Je_rel8_64,
            body[18].Code == Code.Mov_r8_imm8);
    }

    private static bool BooleanLiteral(NativeInstruction instruction) =>
        Registers(instruction, Code.Xor_r32_rm32, NativeRegister.EDX, NativeRegister.EDX) ||
        instruction.Code == Code.Mov_r8_imm8 && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.DL &&
        instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 1;

    private static bool SavedStack(NativeInstruction instruction, Mnemonic operation) =>
        instruction.Mnemonic == operation && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind == OpKind.Immediate8to64 && instruction.GetImmediate(1) == 0x20;

    private static bool SingleRegister(NativeInstruction instruction, Code code, NativeRegister register) =>
        instruction.Code == code && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register;

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == code && instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination && instruction.Op1Kind == OpKind.Register && instruction.Op1Register == source;

    private static bool Branch(NativeInstruction instruction, Code code, ulong target) =>
        instruction.Code == code && instruction.OpCount == 1 && instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool Transfer(NativeInstruction instruction, Code code) =>
        instruction.Code == code && instruction.Length == 5 && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget != 0;
}
