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
public class X64MetadataStaticObjectInt32AddProofTests
{
    [TestCase("CPP2IL_METADATA_GUARD_MOVE_FIXTURE_INPUT")]
    [TestCase("CPP2IL_METADATA_GUARD_MOVE_FRESH_INPUT")]
    public void ExactPlayerBindsBothFieldsAndRejectsNearMisses(string inputVariable)
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
            var owner = app.GetAssemblyByName("MetadataGuardMoveFixture")!.Types
                .Single(type => type.Name == "SharedState");
            var method = owner.Methods.Single(candidate => candidate.Name == "AddBox");
            var parameterMethod = owner.Methods.Single(candidate => candidate.Name == "AddParameter");
            var proof = X64MetadataStaticObjectInt32AddProof.Find(method);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.StaticField, Is.SameAs(owner.Fields.Single(field => field.Name == "Value")));
            Assert.That(proof.InstanceField.DeclaringType.Name, Is.EqualTo("ValueBox"));
            Assert.That(proof.InstanceField.Name, Is.EqualTo("Value"));
            Assert.That(proof.InstanceField.Offset, Is.EqualTo(16));
            Assert.That(X64MetadataStaticInt32AddProof.Find(parameterMethod), Is.Not.Null);
            Assert.That(X64MetadataStaticInt32AddProof.Find(method), Is.Null);

            method.EnsureRawBytes();
            var body = X86Utils.Iterate(method).Take(18).ToArray();
            var shape = X64MetadataStaticObjectInt32AddProof.TryProveShape(body);
            Assert.That(shape, Is.Not.Null);
            Assert.That(shape!.TypeInfoSlot, Is.EqualTo(proof.TypeInfoSlot));
            Assert.That(X86RuntimeNullThrowProof.TryIdentify(app, shape.NullThrowTarget), Is.Not.Null);

            foreach (var defect in new[] { "flags", "guard branch", "flag store", "null test",
                         "null branch", "type slot", "static pointer", "field width",
                         "instance receiver", "return", "terminal helper" })
            {
                var changed = body.ToArray();
                var index = defect switch
                {
                    "flags" => 3,
                    "guard branch" => 4,
                    "flag store" => 7,
                    "null test" => 8,
                    "null branch" => 9,
                    "type slot" => 10,
                    "static pointer" => 11,
                    "field width" => 12,
                    "instance receiver" => 13,
                    "return" => 16,
                    _ => 17,
                };
                var instruction = changed[index];
                switch (defect)
                {
                    case "flags": instruction.Code = Code.Add_r32_rm32; break;
                    case "guard branch": instruction.NearBranch64 = body[10].IP; break;
                    case "flag store": instruction.MemoryDisplacement64++; break;
                    case "null test": instruction.Op1Register = Register.RCX; break;
                    case "null branch": instruction.NearBranch64 = body[16].IP; break;
                    case "type slot": instruction.MemoryDisplacement64 += 8; break;
                    case "static pointer": instruction.MemoryDisplacement64++; break;
                    case "field width": instruction.Code = Code.Mov_r64_rm64; break;
                    case "instance receiver": instruction.MemoryBase = Register.RAX; break;
                    case "return": instruction.Code = Code.Nopd; break;
                    case "terminal helper": instruction.NearBranch64 = 0; break;
                }
                changed[index] = instruction;
                Assert.That(X64MetadataStaticObjectInt32AddProof.TryProveShape(changed), Is.Null,
                    defect);
            }

            var staticField = proof.StaticField;
            try
            {
                staticField.OverrideOffset = staticField.DefaultOffset + 4;
                Assert.That(X64MetadataStaticObjectInt32AddProof.Find(method), Is.Null,
                    "changed static layout is not evidence");
            }
            finally { staticField.OverrideOffset = null; }

            var instanceField = proof.InstanceField;
            try
            {
                instanceField.OverrideOffset = instanceField.DefaultOffset + 4;
                Assert.That(X64MetadataStaticObjectInt32AddProof.Find(method), Is.Null,
                    "changed instance layout is not evidence");
            }
            finally { instanceField.OverrideOffset = null; }

            try
            {
                instanceField.OverrideName = "Changed";
                Assert.That(X64MetadataStaticObjectInt32AddProof.Find(method), Is.Null,
                    "changed field identity is not evidence");
            }
            finally { instanceField.OverrideName = null; }

            var originalBitfield = owner.Definition!.Bitfield;
            try
            {
                owner.Definition.Bitfield |= 1u << 3;
                Assert.That(X64MetadataStaticObjectInt32AddProof.Find(method), Is.Null,
                    "a managed class constructor may reorder effects");
            }
            finally { owner.Definition.Bitfield = originalBitfield; }

            var binding = app.MethodsByAddress[method.UnderlyingPointer];
            binding.Add(parameterMethod);
            try
            {
                Assert.That(X64MetadataStaticObjectInt32AddProof.Find(method), Is.Null,
                    "a shared address does not establish this body");
            }
            finally { binding.Remove(parameterMethod); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
