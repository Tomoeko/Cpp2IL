using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;
using LibCpp2IL;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X86ScalarTruncationFixtureTests
{
    [Test]
    public void NativeScalarTruncationRequiresTheOriginalCanonicalSignatureAndTarget()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_SCALAR_TRUNCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_SCALAR_TRUNCATION_FIXTURE_INPUT to the exact synthetic ScalarTruncationFixture player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var fixture = app.GetAssemblyByName("ScalarTruncationFixture");
            Assert.That(fixture, Is.Not.Null, "Build the public ScalarTruncationFixture; no fallback input is used.");
            var methods = fixture!.Types.SelectMany(t => t.Methods).ToArray();
            Assert.That(methods.Select(m => m.Name), Is.EquivalentTo(new[] { "ToInt32", "ToInt64" }));
            foreach (var method in methods)
            {
                var native = X86Utils.Iterate(method).ToArray();
                var proof = X86ScalarTruncationProof.TryLift(method, native);
                Assert.That(proof, Is.Not.Null);
                var lifted = app.InstructionSet.GetIsilFromMethod(method);
                Assert.That(lifted.Select(i => i.OpCode), Is.EqualTo(new[] { OpCode.FloatTruncateSigned, OpCode.Return }));
                var conversion = lifted[0];
                Assert.That(conversion.Operands[1], Is.EqualTo(new Register(null, "xmm0")));
                Assert.That(conversion.Operands.Skip(2).Cast<Immediate>().Select(i => i.Value),
                    Is.EqualTo(new long[] { 64, method.Name == "ToInt32" ? 32 : 64 }));

                var parameter = method.Parameters[0];
                var originalType = parameter.ParameterType;
                parameter.ParameterType = app.SystemTypes.SystemSingleType;
                Assert.That(X86ScalarTruncationProof.TryLift(method, native), Is.Null);
                parameter.ParameterType = originalType;
                var originalReturn = method.ReturnType;
                method.ReturnType = app.SystemTypes.SystemUInt64Type;
                Assert.That(X86ScalarTruncationProof.TryLift(method, native), Is.Null);
                method.ReturnType = originalReturn;
                var attributes = method.Attributes;
                method.Attributes &= ~MethodAttributes.Static;
                Assert.That(X86ScalarTruncationProof.TryLift(method, native), Is.Null);
                method.Attributes = attributes;
                var implementation = method.ImplAttributes;
                method.ImplAttributes |= MethodImplAttributes.InternalCall;
                Assert.That(X86ScalarTruncationProof.TryLift(method, native), Is.Null);
                method.ImplAttributes = implementation;
                var architecture = app.Binary.InstructionSetId;
                app.Binary.InstructionSetId = DefaultInstructionSets.ARM_V8;
                Assert.That(X86ScalarTruncationProof.TryLift(method, native), Is.Null);
                app.Binary.InstructionSetId = architecture;
                var misplaced = native.ToArray();
                var entry = misplaced[0];
                entry.IP++;
                misplaced[0] = entry;
                Assert.That(X86ScalarTruncationProof.TryLift(method, misplaced), Is.Null);
                Assert.That(X86ScalarTruncationProof.TryLift(method, native), Is.Not.Null);
            }
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }
}
