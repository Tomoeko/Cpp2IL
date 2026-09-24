using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a frame-free x64 Boolean branch whose only exits are direct tail jumps
/// to two uniquely identified managed static methods. The low byte of the second
/// argument is tested; the upper bits of RDX and all other TEST flags are unused.
/// </summary>
internal static class X64BooleanParameterTailBranchProof
{
    internal sealed record Shape(ulong BranchTarget, ulong FallthroughTarget,
        bool BranchOnZero, ulong End);
    internal sealed record Evidence(MethodAnalysisContext BranchTarget,
        MethodAnalysisContext FallthroughTarget, bool BranchOnZero);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> native)
    {
        if (Find(method, native) is not { } proof)
            return null;

        var predicate = new ISIL.Register(null, "boolean_branch_predicate");
        var fallthrough = new ISIL.Register(null, "boolean_fallthrough_result");
        var branch = new ISIL.Register(null, "boolean_branch_result");
        var instructions = new List<ISIL.Instruction>
        {
            new(0, proof.BranchOnZero ? ISIL.OpCode.CheckEqual : ISIL.OpCode.CheckNotEqual,
                predicate, new ISIL.Register(null, "rdx"), new ISIL.Immediate(0)),
            new(1, ISIL.OpCode.ConditionalJump, new ISIL.Immediate(0), predicate),
            new(2, ISIL.OpCode.Call, proof.FallthroughTarget, fallthrough,
                new ISIL.Immediate(0)),
            new(3, ISIL.OpCode.Return, fallthrough),
            new(4, ISIL.OpCode.Call, proof.BranchTarget, branch,
                new ISIL.Immediate(0)),
            new(5, ISIL.OpCode.Return, branch),
        };
        instructions[1].SetOperand(0, instructions[4]);
        return instructions;
    }

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> native)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            !EligibleCaller(method) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            TryProveShape(native) is not { } shape ||
            method.UnderlyingPointer == 0 || native[0].IP != method.UnderlyingPointer ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var callerBindings) ||
            callerBindings is not [var caller] || !ReferenceEquals(caller, method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method))
            return null;

        method.EnsureRawBytes();
        var entry = method.UnderlyingPointer;
        var next = app.GetAddressOfNextFunctionStart(entry);
        if (shape.End <= entry || shape.End > ulong.MaxValue - 15 ||
            entry % 16 != 0)
            return null;
        var paddedEnd = (shape.End + 15) & ~15UL;
        if (next == 0 || next < paddedEnd ||
            (ulong)method.RawBytes.Length < shape.End - entry ||
            !X64NativePaddingProof.HasInt3Padding(pe, shape.End, paddedEnd) ||
            Enumerable.Range(1, checked((int)(paddedEnd - entry) - 1)).Any(
                offset => app.MethodsByAddress.ContainsKey(entry + (ulong)offset)))
            return null;
        var span = unwind.ClassifySpan(entry, paddedEnd);
        if (span.Kind != X64UnwindProof.SpanKind.NoEntry ||
            span.Start != entry || span.End != paddedEnd)
            return null;

        var rawStart = pe.MapVirtualAddressToRaw(entry, false);
        var rawEnd = pe.MapVirtualAddressToRaw(shape.End - 1, false);
        if (rawStart < 0 || rawEnd < rawStart ||
            (ulong)(rawEnd - rawStart) != shape.End - entry - 1 ||
            rawEnd >= pe.GetRawBinaryContent().Length ||
            !TryTarget(app, method, unwind, shape.BranchTarget, out var branchTarget) ||
            !TryTarget(app, method, unwind, shape.FallthroughTarget, out var fallthroughTarget) ||
            ReferenceEquals(branchTarget, fallthroughTarget))
            return null;

        return new Evidence(branchTarget, fallthroughTarget, shape.BranchOnZero);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count < 4)
            return null;
        var first = body[0].Mnemonic == Mnemonic.Nop ? 1 : 0;
        var length = first + 4;
        if (body.Count < length)
            return null;
        var prefix = body.Take(length).ToArray();
        if (prefix.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None) ||
            prefix.Where((instruction, index) => index > 0 &&
                instruction.IP != prefix[index - 1].NextIP).Any())
            return null;

        if (first == 1 && (prefix[0].OpCount != 0 ||
                           prefix[0].FlowControl != FlowControl.Next))
            return null;
        var clear = prefix[first];
        var test = prefix[first + 1];
        var conditional = prefix[first + 2];
        var tail = prefix[first + 3];
        if (!SelfRegister(clear, Mnemonic.Xor, NativeRegister.ECX) ||
            !SelfRegister(test, Mnemonic.Test, NativeRegister.DL) ||
            test.NextIP != conditional.IP || conditional.NextIP != tail.IP ||
            conditional.Mnemonic is not (Mnemonic.Je or Mnemonic.Jne) ||
            conditional.FlowControl != FlowControl.ConditionalBranch ||
            conditional.Op0Kind != OpKind.NearBranch64 ||
            tail.Mnemonic != Mnemonic.Jmp ||
            tail.FlowControl != FlowControl.UnconditionalBranch ||
            tail.Op0Kind != OpKind.NearBranch64 ||
            conditional.NearBranchTarget == tail.NearBranchTarget ||
            conditional.NearBranchTarget == 0 || tail.NearBranchTarget == 0 ||
            Inside(prefix[0].IP, tail.NextIP, conditional.NearBranchTarget) ||
            Inside(prefix[0].IP, tail.NextIP, tail.NearBranchTarget))
            return null;
        return new Shape(conditional.NearBranchTarget, tail.NearBranchTarget,
            conditional.Mnemonic == Mnemonic.Je, tail.NextIP);
    }

    private static bool Inside(ulong start, ulong end, ulong target) =>
        target >= start && target < end;

    private static bool SelfRegister(NativeInstruction instruction, Mnemonic mnemonic,
        NativeRegister register) =>
        instruction.Mnemonic == mnemonic && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == register &&
        instruction.Op1Kind == OpKind.Register && instruction.Op1Register == register &&
        instruction.FlowControl == FlowControl.Next;

    private static bool EligibleCaller(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (!OrdinaryStaticInt32Method(method) ||
            method.DeclaringType is not { Definition: { GenericContainer: null } } owner ||
            !OrdinaryOwner(owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 2,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            definition.InternalParameterData is not [var first, var second] ||
            method.Parameters is not [var firstParameter, var secondParameter] ||
            !UnchangedParameter(method, firstParameter, first, 0,
                Il2CppTypeEnum.IL2CPP_TYPE_I4, app.SystemTypes.SystemInt32Type) ||
            !UnchangedParameter(method, secondParameter, second, 1,
                Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN, app.SystemTypes.SystemBooleanType))
            return false;
        return true;
    }

    private static bool TryTarget(ApplicationAnalysisContext app, MethodAnalysisContext caller,
        X64UnwindProof.Index unwind, ulong address, out MethodAnalysisContext target)
    {
        target = null!;
        if (address < unwind.ImageBase || address - unwind.ImageBase > uint.MaxValue ||
            !unwind.IsExecutableRva((uint)(address - unwind.ImageBase)) ||
            !app.MethodsByAddress.TryGetValue(address, out var bindings) ||
            bindings is not [var bound] || ReferenceEquals(bound, caller) ||
            bound.UnderlyingPointer != address ||
            !ReferenceEquals(bound.DeclaringType, caller.DeclaringType) ||
            !OrdinaryStaticInt32Method(bound) ||
            bound.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, caller.DeclaringType?.Definition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            bound.Parameters.Count != 0 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(bound))
            return false;
        target = bound;
        return true;
    }

    private static bool OrdinaryOwner(TypeAnalysisContext owner) =>
        !owner.IsValueType && !owner.IsInterface && !owner.IsGenericInstance &&
        owner.GenericParameters.Count == 0 && owner.Attributes == owner.DefaultAttributes &&
        owner.Methods.All(candidate => candidate.Name != ".cctor") &&
        owner.Definition is { HasCctor: false,
            PackingSizeIsDefault: true, ClassSizeIsDefault: true,
            RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                NumMods: 0, Byref: 0, Pinned: 0 } };

    private static bool OrdinaryStaticInt32Method(MethodAnalysisContext method) =>
        method.IsStatic && !method.IsVirtual && !method.IsVoid &&
        method.Name is not (".ctor" or ".cctor") &&
        method.Name == method.DefaultName && method.GenericParameters.Count == 0 &&
        method.OverrideReturnType == null &&
        ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemInt32Type) &&
        method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                  MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) == 0;

    private static bool UnchangedParameter(MethodAnalysisContext method,
        ParameterAnalysisContext parameter, LibCpp2IL.Metadata.Il2CppParameterDefinition definition,
        int index, Il2CppTypeEnum rawType, TypeAnalysisContext managedType) =>
        parameter.ParameterIndex == index &&
        ReferenceEquals(parameter.DeclaringMethod, method) &&
        ReferenceEquals(parameter.Definition, definition) &&
        ReferenceEquals(parameter.ParameterType, managedType) &&
        parameter.Attributes == parameter.DefaultAttributes &&
        parameter.Name == parameter.DefaultName &&
        parameter.OverrideParameterType == null &&
        parameter.OverrideAttributes == null &&
        !parameter.UseOverrideDefaultValue && !parameter.IsRef &&
        definition.RawType is { NumMods: 0, Byref: 0, Pinned: 0 } source &&
        source.Type == rawType;
}
