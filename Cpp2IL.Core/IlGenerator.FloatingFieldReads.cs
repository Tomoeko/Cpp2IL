using System;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateFloatingFieldReads(MethodAnalysisContext method)
    {
        if (X64FloatingFieldOperandProof.GetEvidence(method) is not { } recorded)
            return;
        if (X64FloatingFieldOperandProof.Find(method, X86Utils.Iterate(method).FirstOrDefault()) != recorded ||
            method.ControlFlowGraph is not { } graph)
            throw FloatingFieldFailure("the native comparison, signature or field layout changed");

        var instructions = graph.Instructions;
        if (instructions.Count == 0 || graph.EntryBlock.Instructions.Count != 0 || graph.ExitBlock.Instructions.Count != 0 ||
            graph.Blocks.SelectMany(block => block.Instructions).Count() != instructions.Count ||
            instructions[0] is not
            { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                Operands: [LocalVariable captured, FieldReference field] } capture ||
            capture.NativeAddress != recorded.Comparison.IP ||
            !ReferenceEquals(field.Field, recorded.Field) || field.Offset != recorded.Field.Offset)
            throw FloatingFieldFailure("the unique entry field capture or its operation order changed");

        var scalarType = recorded.Width == 32 ? method.AppContext.SystemTypes.SystemSingleType :
            method.AppContext.SystemTypes.SystemDoubleType;
        var abi = new X64CallingConventionResolver().ResolveForParameters(method);
        var receiver = field.Local;
        if (!ReferenceEquals(captured.Type, scalarType) || !receiver.IsThis || receiver.IsMethodInfo ||
            !ReferenceEquals(receiver.Type, method.DeclaringType) || receiver.Register.Version != -1 ||
            method.ParameterOperands.FirstOrDefault() is not Register receiverOperand ||
            abi.FirstOrDefault() is not Register originalReceiver || receiverOperand != originalReceiver ||
            receiver.Register != receiverOperand ||
            method.ParameterLocals.Count(local => local.IsThis) != 1 ||
            !method.ParameterLocals.Contains(receiver) ||
            method.ParameterLocals.Count(local => LocalVariables.GetIncomingParameterIndex(method, local) ==
                recorded.ParameterIndex) != 1 ||
            instructions.Count(instruction => ReferenceEquals(instruction.Destination, captured)) != 1 ||
            OperandEffects.LocalsWithMutableStorage(instructions).Contains(captured))
            throw FloatingFieldFailure("the receiver, incoming argument or captured scalar storage changed");

        var uses = instructions.Where(instruction => OperandEffects.ReadLocals(instruction).Contains(captured)).ToArray();
        var block = graph.FindBlockByInstruction(capture)!;
        if (!ReferenceEquals(graph.Blocks.FirstOrDefault(candidate => candidate != graph.EntryBlock &&
                candidate != graph.ExitBlock && candidate.Instructions.Count != 0), block) ||
            block.Instructions.FirstOrDefault() != capture)
            throw FloatingFieldFailure("the field read is no longer the first emitted operation");
        if (uses.Length == 0 || instructions.Count(instruction => instruction.OpCode == OpCode.FloatCompare &&
                instruction.NativeAddress == recorded.Comparison.IP) != uses.Length)
            throw FloatingFieldFailure("the captured read lost its floating comparison");
        var seenMasks = new System.Collections.Generic.HashSet<long>();
        foreach (var use in uses)
        {
            if (use is not { OpCode: OpCode.FloatCompare, IntegerBitWidth: 0,
                    CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable destination, LocalVariable argument, LocalVariable right,
                        Immediate width, Immediate mask] } ||
                !ReferenceEquals(right, captured) || use.NativeAddress != recorded.Comparison.IP ||
                width.Value != recorded.Width || !seenMasks.Add(mask.Value) ||
                !ReferenceEquals(destination.Type, method.AppContext.SystemTypes.SystemBooleanType) ||
                !ReferenceEquals(argument.Type, scalarType) ||
                !method.ParameterLocals.Contains(argument) ||
                LocalVariables.GetIncomingParameterIndex(method, argument) != recorded.ParameterIndex ||
                method.ParameterOperands[recorded.ParameterIndex + 1] is not Register argumentOperand ||
                abi[recorded.ParameterIndex + 1] is not Register originalArgument ||
                argumentOperand != originalArgument || argument.Register != argumentOperand ||
                mask.Value != FlagMask(destination.Register) ||
                !ReferenceEquals(graph.FindBlockByInstruction(use), block) ||
                block.Instructions.IndexOf(use) <= block.Instructions.IndexOf(capture))
                throw FloatingFieldFailure("the comparison width, native flag, argument or capture origin changed");
        }

        // All live flag evaluations immediately follow the capture. Moving a read past
        // another field access or an effect would change aliasing and null behavior.
        var first = block.Instructions.IndexOf(capture);
        var last = uses.Max(block.Instructions.IndexOf);
        if (block.Instructions.Skip(first + 1).Take(last - first).Any(instruction => !uses.Contains(instruction)))
            throw FloatingFieldFailure("an operation was inserted between the field read and its comparisons");
    }

    private static long FlagMask(Register register) =>
        register.Name == "CF" && register.Number == new Register(null, "CF").Number ? 9 :
        register.Name == "ZF" && register.Number == new Register(null, "ZF").Number ? 10 :
        register.Name == "PF" && register.Number == new Register(null, "PF").Number ? 8 : -1;

    private static DecompilerException FloatingFieldFailure(string detail) =>
        new("Floating field-read proof no longer matches final managed operations: " + detail);
}
