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
public class X64MetadataStaticInt32AddProofTests
{
    [TestCase("CPP2IL_METADATA_GUARD_MOVE_FIXTURE_INPUT", "MetadataGuardMoveFixture")]
    [TestCase("CPP2IL_METADATA_GUARD_PARAMETER_FIXTURE_INPUT", "MetadataGuardParameterFixture")]
    public void ExactPlayerBindsCompleteStaticAddAndRejectsNearMisses(string inputVariable,
        string assemblyName)
    {
        var directory = Environment.GetEnvironmentVariable(inputVariable);
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set " + inputVariable + " to the neutral exact player input.");
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
            var owner = app.GetAssemblyByName(assemblyName)!.Types
                .Single(type => type.Name == "SharedState");
            var method = owner.Methods.Single(candidate => candidate.Name == "AddParameter");
            var evidence = X64MetadataStaticInt32AddProof.Find(method);
            Assert.That(evidence, Is.Not.Null);
            Assert.That(evidence!.Field.Name, Is.EqualTo("Value"));
            Assert.That(evidence.Field.DeclaringType, Is.SameAs(owner));
            if (assemblyName == "MetadataGuardMoveFixture")
            {
                var binding = app.MethodsByAddress[method.UnderlyingPointer];
                var unrelatedMethod = owner.Methods.Single(candidate => candidate.Name == "AddBox");
                binding.Add(unrelatedMethod);
                try
                {
                    Assert.That(X64MetadataStaticInt32AddProof.Find(method), Is.Null,
                        "a shared native address does not prove this method's body");
                }
                finally { binding.Remove(unrelatedMethod); }
            }

            method.EnsureRawBytes();
            var body = X86Utils.Iterate(method).Take(15).ToArray();
            Assert.That(X64MetadataStaticInt32AddProof.TryProveShape(body), Is.Not.Null);
            foreach (var defect in new[] { "flags", "branch", "flag store", "field pointer",
                         "arithmetic" })
            {
                var changed = body.ToArray();
                var index = defect switch
                {
                    "flags" => 3,
                    "branch" => 4,
                    "flag store" => 7,
                    "field pointer" => 9,
                    _ => 11,
                };
                var instruction = changed[index];
                switch (defect)
                {
                    case "flags": instruction.Code = Code.Add_r32_rm32; break;
                    case "branch": instruction.NearBranch64 = body[9].IP; break;
                    case "flag store": instruction.MemoryDisplacement64++; break;
                    case "field pointer": instruction.MemoryDisplacement64++; break;
                    case "arithmetic": instruction.Op1Register = Register.ECX; break;
                }
                changed[index] = instruction;
                Assert.That(X64MetadataStaticInt32AddProof.TryProveShape(changed), Is.Null, defect);
            }

            var field = evidence.Field;
            try
            {
                field.OverrideOffset = field.DefaultOffset + 4;
                Assert.That(X64MetadataStaticInt32AddProof.Find(method), Is.Null,
                    "a changed field layout is not evidence");
            }
            finally { field.OverrideOffset = null; }

            var originalBitfield = owner.Definition!.Bitfield;
            try
            {
                owner.Definition.Bitfield |= 1u << 3;
                Assert.That(X64MetadataStaticInt32AddProof.Find(method), Is.Null,
                    "a managed class constructor can have observable effects");
            }
            finally { owner.Definition.Bitfield = originalBitfield; }

            if (assemblyName == "MetadataGuardMoveFixture")
                Assert.That(X64MetadataStaticInt32AddProof.Find(owner.Methods.Single(candidate =>
                    candidate.Name == "AddBox")), Is.Null, "the null arm needs a separate proof");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
