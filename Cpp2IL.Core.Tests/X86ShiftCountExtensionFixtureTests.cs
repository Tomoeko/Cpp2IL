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
public class X86ShiftCountExtensionFixtureTests
{
    [Test]
    public void NativeShiftCountKeepsItsMaskAndRejectsChangedParameterProfile()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_SHIFT_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_SHIFT_FIXTURE_INPUT to the exact synthetic ShiftFixture player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var fixture = app.GetAssemblyByName("ShiftFixture");
            Assert.That(fixture, Is.Not.Null, "Build the public ShiftFixture source first; no fallback input is used.");
            var methods = fixture!.Types.SelectMany(t => t.Methods).ToArray();
            Assert.That(methods.Select(m => m.Name), Is.EquivalentTo(new[] { "Arithmetic32", "Logical32", "Arithmetic64", "Logical64" }));
            foreach (var method in methods)
            {
                var native = X86Utils.Iterate(method).ToArray();
                Assert.That(X86ShiftCountExtensionProof.Find(method, native), Has.Count.EqualTo(1));
                var lifted = app.InstructionSet.GetIsilFromMethod(method);
                var conversion = lifted.Single(i => i.OpCode == OpCode.IntegerExtend);
                Assert.That(conversion.Operands.Take(2), Is.EqualTo(new IOperand[] { new Register(null, "rcx"), new Register(null, "rdx") }));
                Assert.That(conversion.Operands.Skip(2).Cast<Immediate>().Select(i => i.Value), Is.EqualTo(new long[] { 8, 32, 0 }));
                Assert.That(lifted.Any(i => i.OpCode == OpCode.And && i.Operands.Any(o => o is Immediate { Value: 31 or 63 })), Is.True,
                    "The native count mask must remain in the lifted method.");

                var originalType = method.Parameters[1].ParameterType;
                method.Parameters[1].ParameterType = app.SystemTypes.SystemIntPtrType;
                Assert.That(X86ShiftCountExtensionProof.Find(method, native), Is.Empty);
                method.Parameters[1].ParameterType = originalType;
                var attributes = method.Attributes;
                method.Attributes &= ~MethodAttributes.Static;
                Assert.That(X86ShiftCountExtensionProof.Find(method, native), Is.Empty);
                method.Attributes = attributes;
                var architecture = app.Binary.InstructionSetId;
                app.Binary.InstructionSetId = DefaultInstructionSets.ARM_V8;
                Assert.That(X86ShiftCountExtensionProof.Find(method, native), Is.Empty);
                app.Binary.InstructionSetId = architecture;
                Assert.That(X86ShiftCountExtensionProof.Find(method, native), Has.Count.EqualTo(1));
            }
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }
}
