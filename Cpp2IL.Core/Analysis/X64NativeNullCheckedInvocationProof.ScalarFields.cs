using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using NativeSource = Cpp2IL.Core.Analysis.X64NativeInvocationValues.Source;

namespace Cpp2IL.Core.Analysis;

internal static partial class X64NativeNullCheckedInvocationProof
{
    internal const string BooleanFieldArgumentEvidenceKey = "X64NativeBooleanFieldInvocationArguments";

    private sealed record ScalarFieldArgument(Instruction Definition, ulong Address,
        FieldAnalysisContext Field, TypeAnalysisContext Type, int Offset);
    private sealed record BooleanFieldArgumentLoad(NativeInstruction Load, FieldAnalysisContext Field);

    internal static bool IsScalarFieldArgumentCapture(MethodAnalysisContext caller,
        LocalVariable value, Instruction definition, TypeAnalysisContext type) =>
        TryScalarFieldArgument(caller, value, definition, type, out _);

    private static bool TryScalarFieldArgument(MethodAnalysisContext caller, LocalVariable value,
        Instruction definition, TypeAnalysisContext type, out ScalarFieldArgument argument)
    {
        argument = null!;
        if (!Scalar(type) || !ReferenceEquals(value.Type, type) || Escaped(caller, value) ||
            definition is not { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                NativeAddress: { } address, Operands: [LocalVariable captured, FieldReference access] } ||
            !ReferenceEquals(captured, value) || !ReferenceEquals(access.Field.FieldType, type) ||
            !ReferenceEquals(access.Field.DeclaringType, caller.DeclaringType) || !AccessibleField(caller, access.Field) ||
            !NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, ScalarBits(type)) ||
            !TryOrigin(caller, access.Local, definition, out var owner) || owner.Definition != null || owner.Entry != -1 ||
            !ReferenceEquals(owner.Type, caller.DeclaringType))
            return false;
        argument = new(definition, address, access.Field, type, access.Offset);
        return true;
    }

    private static int ScalarBits(TypeAnalysisContext type) =>
        type.Type == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN ? 8 : 32;

    private static bool BindScalarFieldArgument(MethodAnalysisContext caller, ScalarFieldArgument argument,
        NativeInstruction[] body, X64NativeInvocationValues values, ulong use, out NativeSource source)
    {
        source = default;
        if (argument.Definition.Operands is not [LocalVariable value, FieldReference access] ||
            !TryScalarFieldArgument(caller, value, argument.Definition, argument.Type, out var current) || current != argument ||
            !values.Dominates(argument.Address, use))
            return false;
        var load = body.SingleOrDefault(native => native.IP == argument.Address);
        if (!NativeCurrentOwnerField(caller, load, values, argument.Type, out var field) ||
            !ReferenceEquals(field, argument.Field))
            return false;
        source = new(NativeRegister.None, argument.Address, load.Op0Register.GetFullRegister());
        return true;
    }

    // This proves one captured read's storage and native value. Modifier counts
    // do not establish authored volatility; this path neither removes barriers
    // nor permits replacing the capture with a new field read.
    private static bool NativeCurrentOwnerField(MethodAnalysisContext caller, NativeInstruction load,
        X64NativeInvocationValues values, TypeAnalysisContext type, out FieldAnalysisContext field)
    {
        field = null!;
        var bits = type.IsValueType ? ScalarBits(type) : 64;
        if (caller.IsStatic || load.Op0Kind != OpKind.Register || load.Op1Kind != OpKind.Memory ||
            load.MemoryIndex != NativeRegister.None || load.MemoryDisplacement64 > int.MaxValue ||
            load.MemorySize.GetSize() * 8 != bits ||
            (bits == 8 ? load.Code != Code.Movzx_r32_rm8 :
                bits == 32 ? load.Code != Code.Mov_r32_rm32 : load.Code != Code.Mov_r64_rm64) ||
            !values.Matches(load.IP, load.MemoryBase, 64, new(NativeRegister.RCX)))
            return false;
        var fields = caller.DeclaringType!.Fields.Where(candidate =>
            candidate.Offset == (int)load.MemoryDisplacement64 && ReferenceEquals(candidate.FieldType, type)).ToArray();
        if (fields is not [{ } found] || !AccessibleField(caller, found)) return false;
        var owner = new LocalVariable("native-owner", new ISIL.Register(null, "rcx"), caller.DeclaringType);
        var access = new FieldReference(found, owner, (int)found.Offset);
        if (!(type.IsValueType ? NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, bits) :
                NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access))) return false;
        field = found;
        return true;
    }

    // A memory MOVZX cannot use the generic integer-extension path. Before SSA,
    // authenticate the complete body, current-owner field and its Boolean ABI
    // consumer. Final emission additionally requires the retained managed site.
    internal static bool IsBooleanFieldArgumentLoad(MethodAnalysisContext caller, NativeInstruction load)
    {
        if (load.Code != Code.Movzx_r32_rm8 || load.Op1Kind != OpKind.Memory) return false;
        try
        {
            var loads = caller.GetExtraData<BooleanFieldArgumentLoad[]>(BooleanFieldArgumentEvidenceKey);
            if (loads == null)
            {
                loads = FindBooleanFieldArgumentLoads(caller);
                if (loads.Length == 0) return false;
                caller.PutExtraData(BooleanFieldArgumentEvidenceKey, loads);
                NativeRecoveryProofTracker.Mark(caller, BooleanFieldArgumentEvidenceKey);
            }
            return loads.Any(candidate => candidate.Load == load);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static BooleanFieldArgumentLoad[] FindBooleanFieldArgumentLoads(MethodAnalysisContext caller)
    {
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(caller.AppContext) || caller.IsStatic ||
            !OrdinaryCallerGroup(caller) || !CallerParameters(caller) ||
            X64NativeInstructionReader.ReadRootBody(caller) is not { Length: > 0 and <= 512 } body)
            return [];
        var noReturn = new HashSet<ulong>();
        for (var index = 0; index + 1 < body.Length; index++)
            if (body[index].Code == Code.Call_rel32_64 && body[index + 1].Code == Code.Int3 &&
                X86RuntimeNullThrowProof.TryIdentify(caller.AppContext, body[index].NearBranchTarget) != null)
                noReturn.Add(body[index].IP);
        if (noReturn.Count != 1 || X86CallerExceptionRegionProof.Check(caller, body, noReturn) != null ||
            X64NativeInvocationValues.Create(body, noReturn) is not { } values ||
            !X64NativeInvocationFrameProof.IsValid(caller, body, values)) return [];
        var admitted = new List<BooleanFieldArgumentLoad>();
        foreach (var load in body.Where(native => native.Code == Code.Movzx_r32_rm8 && native.Op1Kind == OpKind.Memory))
        {
            if (!NativeCurrentOwnerField(caller, load, values, caller.AppContext.SystemTypes.SystemBooleanType,
                    out var field)) continue;
            if (body.Any(native => BooleanFieldConsumer(caller, body, values, noReturn, native, load)))
                admitted.Add(new(load, field));
        }
        return admitted.ToArray();
    }

    private static bool BooleanFieldConsumer(MethodAnalysisContext caller, NativeInstruction[] body,
        X64NativeInvocationValues values, HashSet<ulong> noReturn, NativeInstruction call, NativeInstruction load)
    {
        if (call.Code is not (Code.Call_rel32_64 or Code.Jmp_rel32_64) || noReturn.Contains(call.IP) ||
            !caller.AppContext.MethodsByAddress.TryGetValue(call.NearBranchTarget, out var targets) ||
            targets is not [{ } target] || target.IsStatic || target.IsVirtual || !OrdinaryMethod(target) ||
            !HasScalarParameters(target) || !values.Dominates(load.IP, call.IP) ||
            !values.HasCallFrame(call.IP, call.Code == Code.Jmp_rel32_64) ||
            call.Code == Code.Jmp_rel32_64 && caller.ReturnType != target.ReturnType ||
            !values.Matches(call.IP, target.Parameters.Count == 1 ? NativeRegister.R8 : NativeRegister.R9,
                64, new(NativeRegister.None, Literal: 0))) return false;
        var source = new NativeSource(NativeRegister.None, load.IP, load.Op0Register.GetFullRegister());
        if (!target.Parameters.Select((parameter, index) =>
                ReferenceEquals(parameter.ParameterType, caller.AppContext.SystemTypes.SystemBooleanType) &&
                values.Matches(call.IP, index == 0 ? NativeRegister.RDX : NativeRegister.R8, 8, source)).Any(match => match))
            return false;
        // The eventual managed proof owns this same receiver check. At this
        // preprocessing stage admit only an unchanged current-owner reference
        // field whose capture reaches both the native comparison and invocation.
        foreach (var receiverLoad in body.Where(native => native.Code == Code.Mov_r64_rm64 && native.Op1Kind == OpKind.Memory))
        {
            var receiverField = caller.DeclaringType!.Fields.SingleOrDefault(field =>
                field.Offset == (long)receiverLoad.MemoryDisplacement64 && OrdinaryClass(field.FieldType));
            if (receiverField == null || !EligibleTarget(caller, target, receiverField.FieldType) ||
                !NativeCurrentOwnerField(caller, receiverLoad, values, receiverField.FieldType, out _) ||
                !values.Matches(call.IP, NativeRegister.RCX, 64,
                    new(NativeRegister.None, receiverLoad.IP, receiverLoad.Op0Register.GetFullRegister()))) continue;
            for (var index = 0; index + 1 < body.Length; index++)
            {
                var comparison = body[index];
                var branch = body[index + 1];
                if (comparison.Code != Code.Test_rm64_r64 || comparison.Op0Kind != OpKind.Register ||
                    comparison.Op1Kind != OpKind.Register || comparison.Op0Register != comparison.Op1Register ||
                    branch.Mnemonic is not (Mnemonic.Je or Mnemonic.Jne) || branch.Op0Kind != OpKind.NearBranch64 ||
                    !values.Matches(comparison.IP, comparison.Op0Register, 64,
                        new(NativeRegister.None, receiverLoad.IP, receiverLoad.Op0Register.GetFullRegister())) ||
                    !values.Dominates(branch.IP, call.IP)) continue;
                var normal = branch.Mnemonic == Mnemonic.Je ? branch.NextIP : branch.NearBranchTarget;
                var failure = branch.Mnemonic == Mnemonic.Je ? branch.NearBranchTarget : branch.NextIP;
                if (values.Dominates(normal, call.IP) && NativeNullArm(body, noReturn, failure)) return true;
            }
        }
        return false;
    }

    private static bool NativeNullArm(NativeInstruction[] body, HashSet<ulong> noReturn, ulong address)
    {
        var visited = new HashSet<ulong>();
        while (visited.Add(address))
        {
            var native = body.SingleOrDefault(instruction => instruction.IP == address);
            if (noReturn.Contains(address)) return native.Code == Code.Call_rel32_64;
            if (native.Mnemonic == Mnemonic.Nop) { address = native.NextIP; continue; }
            if (native.Code is Code.Jmp_rel8_64 or Code.Jmp_rel32_64 && native.Op0Kind == OpKind.NearBranch64)
            { address = native.NearBranchTarget; continue; }
            return false;
        }
        return false;
    }

    private static bool BooleanFieldArgumentLoadsRetained(MethodAnalysisContext caller, NativeInstruction[] body,
        X64NativeInvocationValues values, List<Site> sites)
    {
        if (!NativeRecoveryProofTracker.Has(caller, BooleanFieldArgumentEvidenceKey)) return true;
        if (caller.GetExtraData<BooleanFieldArgumentLoad[]>(BooleanFieldArgumentEvidenceKey) is not { Length: > 0 } loads)
            return false;
        return loads.All(load => body.Contains(load.Load) &&
            NativeCurrentOwnerField(caller, load.Load, values, caller.AppContext.SystemTypes.SystemBooleanType, out var field) &&
            ReferenceEquals(field, load.Field) && sites.SelectMany(site => site.Arguments).Any(argument =>
                argument.Field is { } capture && capture.Address == load.Load.IP && ReferenceEquals(capture.Field, field)));
    }

    private static bool ScalarFieldArgumentUsesRetained(MethodAnalysisContext caller, List<Site> sites)
    {
        var fields = sites.SelectMany(site => site.Arguments).Select(argument => argument.Field)
            .OfType<ScalarFieldArgument>().Distinct().ToArray();
        foreach (var instruction in caller.ControlFlowGraph!.Instructions)
        {
            foreach (var read in OperandEffects.ReadLocals(instruction))
            {
                var related = fields.FirstOrDefault(field => TryArgument(caller, read, instruction, field.Type,
                    out _, out var argument) && argument.Field == field);
                if (related == null) continue;
                if (sites.Any(site => ReferenceEquals(site.Invocation, instruction)) ||
                    instruction is { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                        Operands: [LocalVariable destination, LocalVariable source] } &&
                    ReferenceEquals(destination.Type, related.Type) && ReferenceEquals(source.Type, related.Type) ||
                    related.Type.Type == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN &&
                    instruction is { OpCode: OpCode.IntegerExtend, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                        Operands: [LocalVariable extended, LocalVariable original, Immediate { Value: 8 },
                            Immediate { Value: 32 }, Immediate { Value: 0 }] } &&
                    ReferenceEquals(original.Type, related.Type) &&
                    (ReferenceEquals(extended.Type, related.Type) ||
                     ReferenceEquals(extended.Type, caller.AppContext.SystemTypes.SystemUInt32Type)))
                    continue;
                return false;
            }
        }
        return true;
    }
}
