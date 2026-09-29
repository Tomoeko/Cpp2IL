using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using MethodDefinition = AsmResolver.DotNet.MethodDefinition;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class IlGeneratorAggregateScalarReadTests
{
    [Test]
    public void ExactAggregateComponentsKeepTheirSignaturesAndRejectChangedProjections()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_AGGREGATE_SCALAR_COMPARE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_AGGREGATE_SCALAR_COMPARE_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var assembly = app.GetAssemblyByName("AggregateScalarCompareFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "ScalarPairComparer");
            var pair = assembly.Types.Single(type => type.Name == "ScalarPair");
            foreach (var method in owner.Methods.Where(method => method.Name.EndsWith("Greater", StringComparison.Ordinal)))
            {
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty);
                Assert.That(method.Parameters.All(parameter => ReferenceEquals(parameter.ParameterType, pair)), Is.True);
                Assert.That(X64AggregateScalarOperandProof.GetEvidence(method), Has.Count.EqualTo(2));
                var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
                Assert.That(definition.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld),
                    Is.EqualTo(2));
                foreach (var mutation in new[] { "component-type", "component-offset", "other-overlap", "layout",
                    "parameter-type", "snapshot-type", "snapshot-name", "read-site", "read-width", "read-offset",
                    "receiver", "argument-name", "argument-version", "duplicate-parameter", "compare-site",
                    "compare-width", "compare-mask", "compare-source", "removed-read", "duplicate-read",
                    "reordered-reads", "extra-effect", "detached-block", "entry-marker-reads", "exit-marker-effect",
                    "native-bytes", "interior-entry" })
                    RejectMutation(method, definition, mutation);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectMutation(MethodAnalysisContext method, MethodDefinition definition, string mutation)
    {
        var graph = method.ControlFlowGraph!;
        var reads = graph.Instructions.Where(instruction => instruction.OpCode == OpCode.Move &&
            instruction.Operands[1] is FieldReference).ToArray();
        var read = reads[0];
        var snapshot = (LocalVariable)read.Operands[0];
        var field = (FieldReference)read.Operands[1];
        var component = field.Field;
        var aggregate = component.DeclaringType;
        var other = aggregate.Fields.Single(candidate => !candidate.IsStatic && candidate != component);
        var argument = field.Local;
        var parameter = method.Parameters.Single(parameter => ReferenceEquals(parameter.ParameterType, aggregate) &&
            Cpp2IL.Core.Analysis.LocalVariables.GetIncomingParameterIndex(method, argument) == parameter.ParameterIndex);
        var comparison = graph.Instructions.First(instruction => instruction.OpCode == OpCode.FloatCompare);
        var compareOperands = comparison.Operands.ToArray();
        var block = graph.FindBlockByInstruction(read)!;
        var blocks = graph.Blocks.ToArray();
        var blockInstructions = blocks.Select(candidate => candidate.Instructions.ToArray()).ToArray();
        var parameterLocals = method.ParameterLocals.ToArray();
        var argumentRegister = argument.Register;
        var snapshotRegister = snapshot.Register;
        var snapshotType = snapshot.Type;
        var readSite = read.NativeAddress;
        var compareSite = comparison.NativeAddress;
        var readWidth = read.IntegerBitWidth;
        var readOffset = field.Offset;
        var bytes = method.RawBytes;
        var interior = method.UnderlyingPointer + 1;
        try
        {
            switch (mutation)
            {
                case "component-type": component.OverrideFieldType = method.AppContext.SystemTypes.SystemInt32Type; break;
                case "component-offset": component.OverrideOffset = component.DefaultOffset + 4; break;
                case "other-overlap": other.OverrideOffset = component.Offset; break;
                case "layout": aggregate.OverrideAttributes = aggregate.DefaultAttributes | TypeAttributes.ExplicitLayout; break;
                case "parameter-type": parameter.OverrideParameterType = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "snapshot-type": snapshot.Type = aggregate; break;
                case "snapshot-name": snapshot.Register.Name = "xmm7"; break;
                case "read-site": read.NativeAddress++; break;
                case "read-width": read.IntegerBitWidth = 32; break;
                case "read-offset": field.Offset = 4 - field.Offset; break;
                case "receiver": field.Local = new LocalVariable("other", argument.Register.Copy(), argument.Type); break;
                case "argument-name": argument.Register.Name = "r9"; break;
                case "argument-version": argument.Register.Version = 7; break;
                case "duplicate-parameter": method.ParameterLocals.Add(new LocalVariable("duplicate", argumentRegister, aggregate)); break;
                case "compare-site": comparison.NativeAddress++; break;
                case "compare-width": comparison.SetOperand(3, new Immediate(64)); break;
                case "compare-mask": comparison.SetOperand(4, new Immediate(((Immediate)comparison.Operands[4]).Value == 9 ? 10 : 9)); break;
                case "compare-source":
                    var sourceIndex = ReferenceEquals(comparison.Operands[1], snapshot) ? 1 : 2;
                    comparison.SetOperand(sourceIndex, new LocalVariable("otherSnapshot", snapshotRegister, snapshotType));
                    break;
                case "removed-read": block.Instructions.Remove(read); break;
                case "duplicate-read": block.Instructions.Insert(0, read); break;
                case "reordered-reads": block.Instructions.Remove(read); block.Instructions.Insert(1, read); break;
                case "extra-effect": block.Instructions.Insert(0, new Instruction(999, OpCode.Move, field, snapshot)); break;
                case "detached-block":
                    var detached = new Block();
                    detached.Instructions.Add(new Instruction(999, OpCode.Return));
                    graph.Blocks.Add(detached);
                    break;
                case "entry-marker-reads":
                    foreach (var moved in reads)
                    {
                        block.Instructions.Remove(moved);
                        graph.EntryBlock.Instructions.Add(moved);
                    }
                    break;
                case "exit-marker-effect": graph.ExitBlock.Instructions.Add(new Instruction(999, OpCode.Move, field, snapshot)); break;
                case "native-bytes":
                    var changed = bytes.AsSpan().ToArray();
                    changed[0] ^= 1;
                    method.RawBytes = new BinarySlice(changed);
                    break;
                case "interior-entry": method.AppContext.MethodsByAddress[interior] = [method]; break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            Assert.That(() => IlGenerator.GenerateIl(method, definition),
                Throws.TypeOf<DecompilerException>().With.Message.Contains("Aggregate scalar-read proof"),
                method.Name + ": " + mutation);
        }
        finally
        {
            component.OverrideFieldType = null;
            component.OverrideOffset = null;
            other.OverrideOffset = null;
            aggregate.OverrideAttributes = null;
            parameter.OverrideParameterType = null;
            snapshot.Type = snapshotType;
            snapshot.Register = snapshotRegister;
            argument.Register = argumentRegister;
            field.Local = argument;
            field.Offset = readOffset;
            read.NativeAddress = readSite;
            read.IntegerBitWidth = readWidth;
            comparison.NativeAddress = compareSite;
            comparison.SetOperands(compareOperands.ToList());
            method.RawBytes = bytes;
            method.AppContext.MethodsByAddress.Remove(interior);
            method.ParameterLocals.Clear();
            method.ParameterLocals.AddRange(parameterLocals);
            graph.Blocks.Clear();
            graph.Blocks.AddRange(blocks);
            for (var index = 0; index < blocks.Length; index++)
            {
                blocks[index].Instructions.Clear();
                blocks[index].Instructions.AddRange(blockInstructions[index]);
            }
        }
    }
}
