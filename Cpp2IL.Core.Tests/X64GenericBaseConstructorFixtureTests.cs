using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64GenericBaseConstructorFixtureTests
{
    [Test]
    public void GenericBaseMethodRefShapeRemainsUnsupportedUntilRuntimeQualification()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_BOOLEAN_GETTER_METADATA_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_BOOLEAN_GETTER_METADATA_FIXTURE_INPUT to the neutral player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
                Directory.EnumerateFiles(input!, "global-metadata.dat", SearchOption.AllDirectories).Single(),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("BooleanGetterMetadataFixture")!.Types
                .Single(type => type.Name == "GenericFieldOwner").Methods.Single(candidate => candidate.Name == ".ctor");
            method.EnsureRawBytes();
            var body = X64NativeInstructionReader.ReadRootBody(method)!;
            Assert.That(X64GenericBaseConstructorProof.TryProveShape(body, out var shape), Is.True);
            Assert.That(app.LibCpp2IlContext.GetMethodGlobalByAddress(shape.MethodSlot)?.Type,
                Is.EqualTo(MetadataUsageType.MethodRef));
            Assert.That(app.MethodsByAddress[shape.Target].Count, Is.GreaterThan(1));
            Assert.That(X64GenericBaseConstructorProof.Find(method), Is.Null);
            Assert.That(X64MetadataInitializationHelperProof.CheckGuardedMethodRef(method).Disposition,
                Is.EqualTo(X64MetadataInitializationHelperProof.MethodRefDisposition.Unsupported));

            foreach (var defect in new[] { "branch", "slot register", "once write", "argument register", "tail kind" })
            {
                var changed = body.ToArray();
                switch (defect)
                {
                    case "branch": changed[4].NearBranch64++; break;
                    case "slot register": changed[5].Op0Register = Register.RDX; break;
                    case "once write": changed[7].Immediate8 = 0; break;
                    case "argument register": changed[8].Op0Register = Register.R8; break;
                    case "tail kind": changed[12].Code = Code.Call_rel32_64; break;
                }
                Assert.That(X64GenericBaseConstructorProof.TryProveShape(changed, out _), Is.False, defect);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
