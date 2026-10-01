using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64GenericInterfaceWrapperProofTests
{
    [Test]
    public void ExactPlayerRetainsGuardedMethodRefShapeWithoutClaimingRecovery()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_GENERIC_DISPATCH_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_GENERIC_DISPATCH_FIXTURE_INPUT to the neutral player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
                Directory.EnumerateFiles(input!, "global-metadata.dat", SearchOption.AllDirectories).Single(),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var wrappers = app.GetAssemblyByName("GenericDispatchFixture")!.Types
                .Single(type => type.Name == "Forwarder").Methods
                .Where(method => method.Name.Contains("Read", StringComparison.Ordinal)).ToArray();
            Assert.That(wrappers, Has.Length.EqualTo(2));
            foreach (var wrapper in wrappers)
            {
                wrapper.EnsureRawBytes();
                var body = X86Utils.Iterate(wrapper).ToArray();
                Assert.That(X64GenericInterfaceWrapperProof.TryProveShape(body, out var shape), Is.True);
                Assert.That(shape.Argument, Is.EqualTo(wrapper.ReturnType.FullName == "System.String" ? 17 : -7));
                Assert.That(app.MethodsByAddress[shape.Target], Has.Count.EqualTo(3));
                Assert.That(app.LibCpp2IlContext.GetMethodGlobalByAddress(shape.MethodSlot)?.Type,
                    Is.EqualTo(MetadataUsageType.MethodRef));
                Assert.That(X64GenericInterfaceWrapperProof.Find(wrapper), Is.Null);
                Assert.That(X64MetadataInitializationHelperProof.CheckGuardedMethodRef(wrapper).Disposition,
                    Is.EqualTo(X64MetadataInitializationHelperProof.MethodRefDisposition.Unsupported));

                foreach (var defect in new[] { "branch", "once write", "MethodInfo load",
                             "signed argument register", "receiver", "tail kind" })
                {
                    var changed = body.ToArray();
                    switch (defect)
                    {
                        case "branch": changed[4].NearBranch64++; break;
                        case "once write": changed[7].Immediate8 = 0; break;
                        case "MethodInfo load": changed[8].Op0Register = Register.RDX; break;
                        case "signed argument register": changed[9].Op0Register = Register.EAX; break;
                        case "receiver": changed[10].Op1Register = Register.RDX; break;
                        case "tail kind": changed[13].Code = Code.Call_rel32_64; break;
                    }
                    Assert.That(X64GenericInterfaceWrapperProof.TryProveShape(changed, out _), Is.False, defect);
                }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
