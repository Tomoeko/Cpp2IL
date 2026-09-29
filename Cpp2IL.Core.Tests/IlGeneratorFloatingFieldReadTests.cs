using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using MethodDefinition = AsmResolver.DotNet.MethodDefinition;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class IlGeneratorFloatingFieldReadTests
{
    [Test]
    public void ExactFieldComparisonsAnalyzeEmitOneReadAndRejectChangedProvenance()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_SCALAR_FIELD_COMPARISON_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_SCALAR_FIELD_COMPARISON_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var owner = app.GetAssemblyByName("ScalarFieldComparisonFixture")!.Types
                .Single(type => type.Name == "ScalarFieldState");
            foreach (var name in new[] { "RetainGreater32", "RetainGreater64" })
            {
                var method = owner.Methods.Single(candidate => candidate.Name == name);
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty);
                Assert.That(X64FloatingFieldOperandProof.GetEvidence(method), Is.Not.Null);
                var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
                Assert.That(definition.CilMethodBody!.Instructions.Count(instruction =>
                    instruction.OpCode == CilOpCodes.Ldfld &&
                    instruction.Operand!.ToString()!.Contains(name.EndsWith("32") ? "Single" : "Double")),
                    Is.EqualTo(1), "all live native flags must share one field snapshot");
                foreach (var mutation in new[]
                {
                    "field-type", "field-offset", "field-flags", "owner-layout", "parameter-type",
                    "capture-site", "capture-width", "capture-type", "field-receiver", "read-offset",
                    "compare-site", "compare-width", "compare-mask", "compare-source", "argument-origin",
                    "duplicate-parameter", "duplicate-read", "removed-read", "reordered-read", "extra-effect",
                    "disconnected-block", "reordered-blocks", "entry-marker-read", "exit-marker-effect",
                    "receiver-register-name", "argument-register-name",
                    "native-bytes", "interior-entry", "address-taken"
                })
                    RejectMutation(method, definition, mutation);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectMutation(MethodAnalysisContext method, MethodDefinition definition, string mutation)
    {
        var graph = method.ControlFlowGraph!;
        var capture = graph.Instructions[0];
        var captured = (LocalVariable)capture.Operands[0];
        var read = (FieldReference)capture.Operands[1];
        var field = read.Field;
        var receiver = read.Local;
        var compare = graph.Instructions.First(instruction => instruction.OpCode == OpCode.FloatCompare);
        var argument = (LocalVariable)compare.Operands[1];
        var compareOperands = compare.Operands.ToArray();
        var parameter = method.Parameters.Single();
        var scalarType = captured.Type;
        var fieldSite = capture.NativeAddress;
        var compareSite = compare.NativeAddress;
        var captureWidth = capture.IntegerBitWidth;
        var blocks = graph.Blocks.ToArray();
        var blockInstructions = blocks.Select(block => block.Instructions.ToArray()).ToArray();
        var block = graph.FindBlockByInstruction(capture)!;
        var parameterLocals = method.ParameterLocals.ToArray();
        var originalBytes = method.RawBytes;
        var originalOffset = read.Offset;
        var receiverRegister = receiver.Register;
        var argumentRegister = argument.Register;
        var interior = method.UnderlyingPointer + 1;
        try
        {
            switch (mutation)
            {
                case "field-type": field.OverrideFieldType = method.AppContext.SystemTypes.SystemInt32Type; break;
                case "field-offset": field.OverrideOffset = field.DefaultOffset + 8; break;
                case "field-flags": field.OverrideAttributes = field.DefaultAttributes | FieldAttributes.Static; break;
                case "owner-layout": method.DeclaringType!.OverrideAttributes = method.DeclaringType.DefaultAttributes | TypeAttributes.ExplicitLayout; break;
                case "parameter-type": parameter.OverrideParameterType = method.AppContext.SystemTypes.SystemInt32Type; break;
                case "capture-site": capture.NativeAddress++; break;
                case "capture-width": capture.IntegerBitWidth = 32; break;
                case "capture-type": captured.Type = method.AppContext.SystemTypes.SystemInt32Type; break;
                case "field-receiver": read.Local = new LocalVariable("otherReceiver", receiver.Register.Copy(), receiver.Type) { IsThis = true }; break;
                case "read-offset": read.Offset += 8; break;
                case "compare-site": compare.NativeAddress++; break;
                case "compare-width": compare.SetOperand(3, new Immediate(scalarType == method.AppContext.SystemTypes.SystemSingleType ? 64 : 32)); break;
                case "compare-mask": compare.SetOperand(4, new Immediate(((Immediate)compare.Operands[4]).Value == 9 ? 10 : 9)); break;
                case "compare-source": compare.SetOperand(2, argument); break;
                case "argument-origin": compare.SetOperand(1, new LocalVariable("otherArgument", argument.Register.Copy(), argument.Type)); break;
                case "receiver-register-name": receiver.Register.Name = "rdx"; break;
                case "argument-register-name": argument.Register.Name = "xmm3"; break;
                case "duplicate-parameter": method.ParameterLocals.Add(new LocalVariable("duplicate", argument.Register.Copy(), argument.Type)); break;
                case "duplicate-read": block.Instructions.Insert(0, capture); break;
                case "removed-read": block.Instructions.Remove(capture); break;
                case "reordered-read": block.Instructions.Remove(capture); block.Instructions.Insert(1, capture); break;
                case "extra-effect": block.Instructions.Insert(1, new Instruction(999, OpCode.Move, read, argument)); break;
                case "disconnected-block":
                    var detached = new Block();
                    detached.Instructions.Add(new Instruction(999, OpCode.Return));
                    graph.Blocks.Add(detached);
                    break;
                case "reordered-blocks":
                    var otherBlock = graph.Blocks.First(candidate => candidate != block && candidate.Instructions.Count != 0);
                    graph.Blocks.Remove(otherBlock);
                    graph.Blocks.Insert(graph.Blocks.IndexOf(block), otherBlock);
                    break;
                case "entry-marker-read": block.Instructions.Remove(capture); graph.EntryBlock.Instructions.Add(capture); break;
                case "exit-marker-effect": graph.ExitBlock.Instructions.Add(new Instruction(999, OpCode.Move, read, argument)); break;
                case "native-bytes":
                    var changed = originalBytes.AsSpan().ToArray();
                    changed[0] ^= 1;
                    method.RawBytes = new BinarySlice(changed);
                    break;
                case "interior-entry": method.AppContext.MethodsByAddress[interior] = [method]; break;
                case "address-taken": block.Instructions.Add(new Instruction(999, OpCode.Move, argument, new AddressOf(captured))); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            Assert.That(() => IlGenerator.GenerateIl(method, definition),
                Throws.TypeOf<DecompilerException>().With.Message.Contains("Floating field-read proof"),
                method.Name + ": " + mutation);
        }
        finally
        {
            field.OverrideFieldType = null;
            field.OverrideOffset = null;
            field.OverrideAttributes = null;
            method.DeclaringType!.OverrideAttributes = null;
            parameter.OverrideParameterType = null;
            capture.NativeAddress = fieldSite;
            capture.IntegerBitWidth = captureWidth;
            captured.Type = scalarType;
            read.Local = receiver;
            receiver.Register = receiverRegister;
            argument.Register = argumentRegister;
            read.Offset = originalOffset;
            compare.NativeAddress = compareSite;
            compare.SetOperands(compareOperands.ToList());
            method.RawBytes = originalBytes;
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
