using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;
using NativeInstruction = Iced.Intel.Instruction;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64StackAggregateCallRecovery
{
    // Called after ordinary lifting and native exit qualification, before stack
    // slots lose their byte ranges and before SSA assigns storage versions.
    internal static bool TryRewriteArguments(MethodAnalysisContext method, IReadOnlyList<NativeInstruction> native,
        List<ManagedInstruction> instructions)
    {
        if (X64StackAggregateCallProof.Find(method) is not { } proof || native.Count == 0 ||
            native.Count > proof.Body.Length || !native.SequenceEqual(proof.Body[..native.Count].ToArray()) ||
            proof.Body[native.Count..].ToArray().Any(instruction => instruction.Code != Iced.Intel.Code.Int3)) return false;
        var replacements = new List<(ManagedInstruction Instruction, IOperand Destination, IOperand Source)>();
        var suppressed = new List<ManagedInstruction>();
        foreach (var site in proof.Sites)
        {
            var copy = site.Copy;
            var snapshot = new Register(null, X64StackAggregateCallProof.SnapshotPrefix + copy.Call.ToString("X"));
            foreach (var address in copy.ReplacedAddresses.Append(copy.Address))
            {
                if (instructions.Where(instruction => instruction.NativeAddress == address).ToArray() is not
                    [{ OpCode: OpCode.Move, CallSemantics: CallSemantics.Direct } instruction]) return false;
                if (address == copy.FirstRead)
                    replacements.Add((instruction, snapshot, new Register(null, X86Utils.GetRegisterName(copy.Source))));
                else if (address == copy.Address)
                    replacements.Add((instruction, new Register(null, X86Utils.GetRegisterName(copy.Argument)), snapshot));
                else suppressed.Add(instruction);
            }
        }
        // All replacements are checked before changing the graph. Keep the
        // existing instruction objects so branch targets do not become detached.
        foreach (var (instruction, destination, source) in replacements)
        {
            instruction.OpCode = OpCode.Move;
            instruction.IntegerBitWidth = 0;
            instruction.SetOperands(destination, source);
        }
        foreach (var instruction in suppressed)
        {
            instruction.OpCode = OpCode.Nop;
            instruction.IntegerBitWidth = 0;
            instruction.SetOperands();
        }
        method.PutExtraData(X64StackAggregateCallProof.EvidenceKey, proof);
        NativeRecoveryProofTracker.Mark(method, X64StackAggregateCallProof.EvidenceKey);
        return true;
    }

    internal static void ValidateFinalGraph(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (!X64StackAggregateCallProof.HasEvidence(method)) return;
        if (method.GetExtraData<X64StackAggregateCallProof.Evidence>(X64StackAggregateCallProof.EvidenceKey) is not { } proof ||
            !proof.IsUnchanged() || method.ControlFlowGraph is not { } graph || !MatchesOutput(method, definition))
            throw Failure("the original bytes, layout, signature, alias, private lifetime or argument identity changed");
        var instructions = graph.Instructions;
        // An independently unresolved hidden return or other native operation
        // keeps its existing strict diagnostic. Argument evidence cannot clear it.
        if (instructions.Any(instruction => instruction.OpCode is OpCode.NotImplemented or OpCode.Invalid)) return;
        if (graph.EntryBlock.Instructions.Count != 0 || graph.ExitBlock.Instructions.Count != 0 ||
            graph.Blocks.SelectMany(block => block.Instructions).Count() != instructions.Count)
            throw Failure("the typed graph has detached or duplicated operations");
        foreach (var site in proof.Sites)
        {
            if (instructions.Where(instruction => instruction.NativeAddress == site.Copy.Call && instruction.IsCall).ToArray()
                is not [var call] || !ReferenceEquals(call.Operands[0], site.Target) ||
                call.CallSemantics is not (CallSemantics.Direct or CallSemantics.NullCheckedInstance))
                throw Failure("the consuming managed call changed");
            var argumentIndex = (call.OpCode == OpCode.CallVoid ? 1 : 2) +
                (site.Target.IsStatic ? 0 : 1) + site.TargetParameter;
            if (argumentIndex >= call.Operands.Count || call.Operands[argumentIndex] is not LocalVariable argument ||
                !ReferenceEquals(argument.Type, site.Type) ||
                !FromOriginalParameter(argument, site.SourceParameter, new HashSet<LocalVariable>()))
                throw Failure("the whole by-value snapshot changed or became mutable/addressed storage");
            foreach (var address in site.Copy.ReplacedAddresses.Where(address => address != site.Copy.FirstRead))
                if (instructions.Any(instruction => instruction.NativeAddress == address && instruction.OpCode != OpCode.Nop))
                    throw Failure("a removed packed piece acquired another managed effect");

            bool FromOriginalParameter(LocalVariable local, int parameter, HashSet<LocalVariable> visited)
            {
                if (!visited.Add(local) || visited.Count > 64 || !ReferenceEquals(local.Type, site.Type) ||
                    OperandEffects.LocalsWithMutableStorage(instructions).Contains(local)) return false;
                var definitions = instructions.Where(instruction => ReferenceEquals(instruction.Destination, local)).ToArray();
                if (method.ParameterLocals.Contains(local) && LocalVariables.GetIncomingParameterIndex(method, local) == parameter)
                    return definitions.Length == 0;
                if (definitions is not [{ OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable, LocalVariable source], NativeAddress: { } address }] ||
                    proof.Body.ToArray().All(native => native.IP != address)) return false;
                if (address != site.Copy.FirstRead && address != site.Copy.Address && proof.Body.ToArray().Single(native =>
                    native.IP == address).Code is not (Iced.Intel.Code.Mov_r64_rm64 or Iced.Intel.Code.Mov_rm64_r64)) return false;
                return FromOriginalParameter(source, parameter, visited);
            }
        }
    }

    internal static bool MatchesOutput(MethodAnalysisContext method, MethodDefinition definition)
    {
        if (method.GetExtraData<X64StackAggregateCallProof.Evidence>(X64StackAggregateCallProof.EvidenceKey) is not { } proof ||
            !proof.IsUnchanged() || !OriginalOutputMethod(method, definition)) return false;
        return proof.Sites.ToArray().All(site => OriginalOutputMethod(site.Target,
            site.Target.GetExtraData<MethodDefinition>("AsmResolverMethod")) && OriginalOutputAggregate(site.Type));
    }

    private static bool OriginalOutputMethod(MethodAnalysisContext method, MethodDefinition? output)
    {
        var owner = method.DeclaringType?.GetExtraData<TypeDefinition>("AsmResolverType");
        var ordinal = method.DeclaringType?.Methods.IndexOf(method) ?? -1;
        return output?.Signature is { } signature && owner != null && ordinal >= 0 && ordinal < owner.Methods.Count &&
            OriginalOutputOwner(method.DeclaringType!, owner) &&
            ReferenceEquals(method.GetExtraData<MethodDefinition>("AsmResolverMethod"), output) &&
            ReferenceEquals(output.DeclaringType, owner) && ReferenceEquals(owner.Methods[ordinal], output) &&
            output.Name == method.Name && (ushort)output.Attributes == (ushort)method.Attributes &&
            (ushort)output.ImplAttributes == (ushort)method.ImplAttributes &&
            signature.Attributes == (CallingConventionAttributes.Default | (method.IsStatic ? 0 : CallingConventionAttributes.HasThis)) &&
            signature.SentinelParameterTypes.Count == 0 &&
            signature.HasThis == !method.IsStatic && !signature.ExplicitThis && signature.GenericParameterCount == 0 &&
            output.GenericParameters.Count == 0 && signature.ParameterTypes.Count == method.Parameters.Count &&
            output.ParameterDefinitions.Count == method.Parameters.Count &&
            SignatureComparer.Default.Equals(signature.ReturnType, method.ReturnType.ToTypeSignature()) &&
            method.Parameters.Select((parameter, index) => output.ParameterDefinitions[index].Sequence == index + 1 &&
                output.ParameterDefinitions[index].Name == parameter.Name &&
                (ushort)output.ParameterDefinitions[index].Attributes == (ushort)parameter.Attributes &&
                SignatureComparer.Default.Equals(signature.ParameterTypes[index], parameter.ParameterType.ToTypeSignature()))
                .All(matches => matches);
    }

    private static bool OriginalOutputOwner(TypeAnalysisContext type, TypeDefinition output)
    {
        if (type.Definition is not { } original ||
            type.DeclaringAssembly.GetExtraData<AssemblyDefinition>("AsmResolverAssembly") is not { ManifestModule: { } module } assembly ||
            !ReferenceEquals(output.DeclaringModule, module) || !ReferenceEquals(module.Assembly, assembly) ||
            module.Name != type.DeclaringAssembly.CleanAssemblyName + ".dll" ||
            assembly.Name != type.DeclaringAssembly.Name || assembly.Version != type.DeclaringAssembly.Version ||
            (assembly.Culture?.ToString() ?? "") != (type.DeclaringAssembly.Culture ?? "") ||
            (uint)assembly.Attributes != type.DeclaringAssembly.Flags ||
            (uint)assembly.HashAlgorithm != type.DeclaringAssembly.HashAlgorithm ||
            !(assembly.PublicKey ?? []).SequenceEqual(type.DeclaringAssembly.PublicKey ?? []) ||
            !ReferenceEquals(type.GetExtraData<TypeDefinition>("AsmResolverType"), output) ||
            !ReferenceEquals(output.DeclaringType, type.DeclaringType?.GetExtraData<TypeDefinition>("AsmResolverType")) ||
            output.Name != type.Name || (output.Namespace?.ToString() ?? "") != type.Namespace ||
            (uint)output.Attributes != (uint)type.Attributes || output.GenericParameters.Count != type.GenericParameters.Count ||
            !SignatureComparer.Default.Equals(output.BaseType, type.BaseType?.ToTypeSignature().ToTypeDefOrRef())) return false;
        var expected = new TypeDefinition(type.Namespace, type.Name, (AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes)type.Attributes);
        AsmResolverDllOutputFormat.ConfigureTypeLayout(original, expected);
        return output.ClassLayout?.PackingSize == expected.ClassLayout?.PackingSize &&
            output.ClassLayout?.ClassSize == expected.ClassLayout?.ClassSize &&
            (output.ClassLayout == null || ReferenceEquals(output.ClassLayout.Parent, output));
    }

    private static bool OriginalOutputAggregate(TypeAnalysisContext type)
    {
        var output = type.GetExtraData<TypeDefinition>("AsmResolverType");
        if (output == null || !OriginalOutputOwner(type, output) || output.DeclaringType != null || output.GenericParameters.Count != 0 ||
            output.Fields.Count != type.Fields.Count) return false;
        for (var ordinal = 0; ordinal < type.Fields.Count; ordinal++)
        {
            var original = type.Fields[ordinal];
            var field = original.GetExtraData<FieldDefinition>("AsmResolverField");
            if (field == null || !ReferenceEquals(output.Fields[ordinal], field) || !ReferenceEquals(field.DeclaringType, output) ||
                field.Name != original.Name || (ushort)field.Attributes != (ushort)original.Attributes ||
                field.Signature is not { Attributes: CallingConventionAttributes.Field } signature ||
                field.FieldOffset != (output.IsExplicitLayout && !original.IsStatic ? original.Offset : (int?)null) ||
                !original.IsStatic && (field.Constant != null || field.FieldRva != null || field.MarshalDescriptor != null) ||
                !SignatureComparer.Default.Equals(signature.FieldType, original.FieldType.ToTypeSignature())) return false;
        }
        return true;
    }

    private static DecompilerException Failure(string detail) =>
        new("Stack aggregate argument reconstruction lost its byte-complete proof: " + detail);
}
