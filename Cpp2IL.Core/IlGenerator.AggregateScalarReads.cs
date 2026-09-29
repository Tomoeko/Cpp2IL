using System;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateAggregateScalarReads(MethodAnalysisContext method)
    {
        if (X64AggregateScalarOperandProof.GetEvidence(method) is not { Count: > 0 } recorded)
            return;
        if (method.ControlFlowGraph is not { } graph)
            throw AggregateScalarFailure("the typed graph is missing");
        var instructions = graph.Instructions;
        if (graph.EntryBlock.Instructions.Count != 0 || graph.ExitBlock.Instructions.Count != 0 ||
            graph.Blocks.SelectMany(block => block.Instructions).Count() != instructions.Count)
            throw AggregateScalarFailure("the graph has detached or duplicated operations");
        var native = X64AggregateScalarOperandProof.ReadBody(method) ??
            throw AggregateScalarFailure("the exact file-backed function boundary changed");
        var abi = new X64CallingConventionResolver().ResolveForParameters(method);
        var reads = new System.Collections.Generic.List<ManagedInstruction>();
        foreach (var evidence in recorded.OrderBy(evidence => evidence.Shape.Load.IP))
        {
            var load = native.SingleOrDefault(instruction => instruction.IP == evidence.Shape.Load.IP);
            if (X64AggregateScalarOperandProof.Find(method, load) != evidence ||
                instructions.Where(instruction => instruction.NativeAddress == load.IP && instruction.OpCode == OpCode.Move).ToArray() is not
                    [{ OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                        Operands: [LocalVariable captured, FieldReference field] } read] ||
                !ReferenceEquals(field.Field, evidence.Field) || field.Offset != evidence.Shape.ComponentOffset ||
                !ReferenceEquals(captured.Type, method.AppContext.SystemTypes.SystemSingleType) ||
                captured.Register.Number != new ManagedRegister(null, SnapshotRegisterName(load)).Number ||
                captured.Register.Name != SnapshotRegisterName(load) ||
                instructions.Count(instruction => ReferenceEquals(instruction.Destination, captured)) != 1 ||
                OperandEffects.LocalsWithMutableStorage(instructions).Contains(captured))
                throw AggregateScalarFailure("the native spill, component field, scalar read or storage changed");

            var parameterIndex = evidence.ParameterIndex;
            var operandIndex = parameterIndex + (method.IsStatic ? 0 : 1);
            var argument = field.Local;
            if (argument.IsThis || argument.IsMethodInfo ||
                !ReferenceEquals(argument.Type, evidence.Field.DeclaringType) ||
                !method.ParameterLocals.Contains(argument) ||
                LocalVariables.GetIncomingParameterIndex(method, argument) != parameterIndex ||
                method.ParameterLocals.Count(local => LocalVariables.GetIncomingParameterIndex(method, local) == parameterIndex) != 1 ||
                method.ParameterOperands[operandIndex] is not ManagedRegister original ||
                abi[operandIndex] is not ManagedRegister expected || original != expected || argument.Register != original)
                throw AggregateScalarFailure("the original aggregate ABI argument changed");

            var uses = instructions.Where(instruction => OperandEffects.ReadLocals(instruction).Contains(captured)).ToArray();
            var comparisonSites = new System.Collections.Generic.HashSet<ulong>(native.Where(instruction => instruction.IP >= load.IP &&
                instruction.Code is Code.Comiss_xmm_xmmm32 or Code.Ucomiss_xmm_xmmm32 &&
                (load.Code == Code.Movss_xmm_xmmm32
                    ? instruction.Op0Register == load.Op0Register || instruction.Op1Register == load.Op0Register
                    : instruction.IP == load.IP))
                .Select(instruction => instruction.IP));
            if (uses.Length == 0 || !uses.SequenceEqual(instructions.Where(instruction =>
                    instruction.OpCode == OpCode.FloatCompare && instruction.NativeAddress is { } site &&
                    comparisonSites.Contains(site))))
                throw AggregateScalarFailure("the scalar snapshot lost its native comparisons");
            var flags = new System.Collections.Generic.HashSet<(ulong, long)>();
            foreach (var use in uses)
            {
                if (use is not { OpCode: OpCode.FloatCompare, IntegerBitWidth: 0,
                        CallSemantics: CallSemantics.Direct,
                        Operands: [LocalVariable destination, LocalVariable left, LocalVariable right,
                            Immediate { Value: 32 }, Immediate mask], NativeAddress: { } address } ||
                    !ReferenceEquals(destination.Type, method.AppContext.SystemTypes.SystemBooleanType) ||
                    !ReferenceEquals(left.Type, method.AppContext.SystemTypes.SystemSingleType) ||
                    !ReferenceEquals(right.Type, method.AppContext.SystemTypes.SystemSingleType) ||
                    !IsRecordedSnapshot(left) || !IsRecordedSnapshot(right) ||
                    mask.Value != FlagMask(destination.Register) ||
                    native.SingleOrDefault(instruction => instruction.IP == address) is not
                        { Code: Code.Comiss_xmm_xmmm32 or Code.Ucomiss_xmm_xmmm32,
                            Op0Kind: OpKind.Register } comparison ||
                    !flags.Add((address, mask.Value)) ||
                    load.Code == Code.Movss_xmm_xmmm32 &&
                        comparison.Op0Register == load.Op0Register && !ReferenceEquals(left, captured) ||
                    load.Code == Code.Movss_xmm_xmmm32 &&
                        comparison.Op1Register == load.Op0Register && !ReferenceEquals(right, captured) ||
                    load.Code != Code.Movss_xmm_xmmm32 && !ReferenceEquals(right, captured) ||
                    left.Register.Name != X86Utils.GetRegisterName(comparison.Op0Register) ||
                    left.Register.Number != new ManagedRegister(null, left.Register.Name).Number ||
                    (comparison.Op1Kind == OpKind.Register
                        ? right.Register.Name != X86Utils.GetRegisterName(comparison.Op1Register) ||
                          right.Register.Number != new ManagedRegister(null, right.Register.Name).Number
                        : comparison.Op1Kind != OpKind.Memory || !IsMemoryComparisonSnapshot(right, comparison)))
                    throw AggregateScalarFailure("the scalar lane, native comparison or flag outcome changed");
            }
            reads.Add(read);
        }

        // This bounded proof replaces only the initial private spill/read sequence.
        // A new managed effect cannot precede those immutable parameter snapshots.
        var firstBlock = graph.Blocks.FirstOrDefault(block => block != graph.EntryBlock && block != graph.ExitBlock &&
            block.Instructions.Count != 0);
        if (firstBlock == null || !firstBlock.Instructions.Take(reads.Count).SequenceEqual(reads) ||
            !instructions.Take(reads.Count).SequenceEqual(reads))
            throw AggregateScalarFailure("component snapshots are no longer the first emitted operations");
        return;

        // Every scalar input must close to an independently revalidated projection.
        // Mixed incoming scalars and values written by other native operations need
        // their own source proof; a matching XMM name or type is insufficient.
        bool IsRecordedSnapshot(LocalVariable operand) => recorded.Any(evidence =>
            instructions.Where(instruction => instruction.NativeAddress == evidence.Shape.Load.IP &&
                instruction.OpCode == OpCode.Move).ToArray() is
                [{ OpCode: OpCode.Move, Operands: [LocalVariable value, FieldReference] }] &&
            ReferenceEquals(value, operand));

        bool IsMemoryComparisonSnapshot(LocalVariable operand, Iced.Intel.Instruction comparison) =>
            recorded.Any(evidence => evidence.Shape.Load == comparison &&
                instructions.Where(instruction => instruction.NativeAddress == comparison.IP &&
                    instruction.OpCode == OpCode.Move).ToArray() is
                    [{ Operands: [LocalVariable value, FieldReference] }] && ReferenceEquals(value, operand));
    }

    private static string SnapshotRegisterName(Iced.Intel.Instruction read) => read.Code == Code.Movss_xmm_xmmm32
        ? X86Utils.GetRegisterName(read.Op0Register) : X64AggregateScalarOperandProof.ComparisonCaptureName;

    private static DecompilerException AggregateScalarFailure(string detail) =>
        new("Aggregate scalar-read proof no longer matches final managed operations: " + detail);
}
