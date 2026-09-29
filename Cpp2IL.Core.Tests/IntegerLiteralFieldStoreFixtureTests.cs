using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.OutputFormats;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player control for signed bits and preserved receiver provenance.</summary>
[NonParallelizable]
public class IntegerLiteralFieldStoreFixtureTests
{
    [Test]
    public void ImmediateStoresBindNativeBitsReceiverLayoutAndOrderedProducerEffects()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_INTEGER_LITERAL_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_INTEGER_LITERAL_STORE_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var assembly = app.GetAssemblyByName("IntegerLiteralStoreFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "IntegerStoreOwner");
            var target = assembly.Types.Single(type => type.Name == "IntegerStoreTarget");
            var neighbor = target.Fields.Single(field => field.Name == "Neighbor");
            foreach (var name in new[] { "SignedNegative", "SignedMinimum", "SignedMaximum", "UnsignedHigh", "UnsignedMaximum", "SetParameter" })
            {
                var method = owner.Methods.Single(candidate => candidate.Name == name);
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty);
                Assert.That(method.ControlFlowGraph!.Instructions.Any(instruction => instruction.OpCode == OpCode.RuntimeNullThrow), Is.False, name);
                var evidence = method.NullCheckedFieldAccesses.Single();
                var literal = (Immediate)evidence.StoredValue!;
                Assert.That(LiteralFieldStoreProof.IsValidFor(method, evidence.Operation, evidence.Access, literal), Is.True);
                Assert.That(evidence.IsValidFor(method), Is.True);

                void Reject(Action mutate, Action restore)
                {
                    try
                    {
                        mutate();
                        Assert.That(evidence.IsValidFor(method), Is.False);
                        var definition = method.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!;
                        Assert.That(() => IlGenerator.GenerateIl(method, definition),
                            Throws.TypeOf<DecompilerException>().With.Message.Contains("Null-checked field access marker"));
                    }
                    finally { restore(); }
                    Assert.That(evidence.IsValidFor(method), Is.True);
                }
                Reject(() => evidence.Field.OverrideFieldType = app.SystemTypes.SystemInt64Type,
                    () => evidence.Field.OverrideFieldType = null);
                Reject(() => neighbor.OverrideOffset = evidence.Offset, () => neighbor.OverrideOffset = null);
                var originalAddress = evidence.Operation.NativeAddress;
                Reject(() => evidence.Operation.NativeAddress = originalAddress + 1,
                    () => evidence.Operation.NativeAddress = originalAddress);
                var originalValue = evidence.Operation.Operands[1];
                Reject(() => evidence.Operation.SetOperand(1, new Immediate(literal.Value ^ 1)),
                    () => evidence.Operation.SetOperand(1, originalValue));
                if (method.ParameterLocals.Contains(evidence.Receiver))
                {
                    var originalRegister = evidence.Receiver.Register;
                    Reject(() => evidence.Receiver.Register = new Register(originalRegister.Number, "rdx"),
                        () => evidence.Receiver.Register = originalRegister);
                }
                if (name == "SignedMinimum")
                {
                    var origin = method.ControlFlowGraph.Instructions.Single(instruction =>
                        ReferenceEquals(instruction.Destination, evidence.Receiver));
                    var address = origin.NativeAddress;
                    Reject(() => origin.NativeAddress = originalAddress,
                        () => origin.NativeAddress = address);
                }
                var output = method.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!;
                Assert.That(() => IlGenerator.GenerateIl(method, output), Throws.Nothing);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
