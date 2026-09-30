using System;
using System.IO;
using System.Linq;
using AsmResolver.DotNet;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64ScalarFloatConversionFixtureTests
{
    [TestCase("Widen")]
    [TestCase("Narrow")]
    public void ExactConversionRequiresUnchangedCacheSignatureTypedValueAndRetainedProof(string name)
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_SCALAR_FLOAT_CONVERSION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_SCALAR_FLOAT_CONVERSION_FIXTURE_INPUT to the neutral conversion player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
                Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var method = app.GetAssemblyByName("ScalarFloatConversionFixture")!.Types.SelectMany(type => type.Methods)
                .Single(method => method.Name == name);
            Assert.That(X64ScalarFloatConversionProof.Find(method), Is.Not.Null);
            var bytes = method.RawBytes;
            try
            {
                var changed = bytes.ToArray();
                changed[0] ^= 1;
                method.RawBytes = new BinarySlice(changed);
                Assert.That(X64ScalarFloatConversionProof.Find(method), Is.Null);
            }
            finally { method.RawBytes = bytes; }
            var raw = method.Parameters[0].Definition!.RawType!;
            var kind = raw.Type;
            try
            {
                raw.Type = Il2CppTypeEnum.IL2CPP_TYPE_I4;
                Assert.That(X64ScalarFloatConversionProof.Find(method), Is.Null);
            }
            finally { raw.Type = kind; }
            method.Analyze();
            Assert.That(X64ScalarFloatConversionProof.IsValidFor(method), Is.True);
            var conversion = method.ControlFlowGraph!.Instructions.Single(instruction => instruction.OpCode == OpCode.FloatConvert);
            var source = conversion.Operands[1];
            try
            {
                conversion.SetOperand(1, new LocalVariable("substituted", new Register(875, "substituted"), ((LocalVariable)source).Type));
                Assert.That(X64ScalarFloatConversionProof.IsValidFor(method), Is.False);
            }
            finally { conversion.SetOperand(1, source); }
            var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
            var body = method.ControlFlowGraph.Blocks.Single(block => block.Instructions.Contains(conversion));
            body.Instructions.Remove(conversion);
            method.ControlFlowGraph.EntryBlock.Instructions.Add(conversion);
            try
            {
                Assert.That(X64ScalarFloatConversionProof.IsValidFor(method), Is.False);
                Assert.That(() => IlGenerator.GenerateIl(method, definition), Throws.TypeOf<DecompilerException>());
            }
            finally { method.ControlFlowGraph.EntryBlock.Instructions.Clear(); body.Instructions.Insert(0, conversion); }
            Assert.That(X64ScalarFloatConversionProof.IsValidFor(method), Is.True);
            Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
            method.PutExtraData<object>(X64ScalarFloatConversionProof.EvidenceKey, null!);
            Assert.That(X64ScalarFloatConversionProof.HasEvidence(method), Is.True);
            Assert.That(() => IlGenerator.GenerateIl(method, definition),
                Throws.TypeOf<DecompilerException>().With.Message.Contains("Floating conversion"));
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
