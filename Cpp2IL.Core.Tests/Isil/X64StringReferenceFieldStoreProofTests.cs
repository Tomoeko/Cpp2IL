using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64StringReferenceFieldStoreProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerRequiresStringMetadataAndTheOriginalNativeEffects()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_STRING_REFERENCE_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_STRING_REFERENCE_STORE_FIXTURE_INPUT to the synthetic player-input directory.");
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
            var assembly = app.GetAssemblyByName("StringReferenceStoreFixture")!;
            var method = assembly.Types.SelectMany(type => type.Methods)
                .Single(candidate => candidate.Name == "StoreText");
            var native = X86Utils.Iterate(method).ToArray();
            var evidence = X64ReferenceFieldStoreProof.Find(method, native);
            Assert.That(evidence?.Field.Name, Is.EqualTo("Text"));
            Assert.That(X64ReferenceFieldStoreProof.TryLift(method, native)?.Select(i => i.OpCode),
                Is.EqualTo(new[] { ISIL.OpCode.Move, ISIL.OpCode.Return }));
            Assert.That(app.InstructionSet.GetIsilFromMethod(method).Select(i => i.OpCode),
                Is.EqualTo(new[] { ISIL.OpCode.Move, ISIL.OpCode.Return }));

            var field = evidence!.Field;
            try
            {
                field.OverrideOffset = field.DefaultOffset + 8;
                Assert.That(X64ReferenceFieldStoreProof.Find(method, native), Is.Null,
                    "a neighboring reference cannot replace the metadata field");
            }
            finally { field.OverrideOffset = null; }
            try
            {
                field.OverrideFieldType = app.SystemTypes.SystemObjectType;
                Assert.That(X64ReferenceFieldStoreProof.Find(method, native), Is.Null,
                    "the selected field must still be string");
            }
            finally { field.OverrideFieldType = null; }
            try
            {
                method.Parameters[1].OverrideParameterType = app.SystemTypes.SystemObjectType;
                Assert.That(X64ReferenceFieldStoreProof.Find(method, native), Is.Null,
                    "the stored argument must still be string");
            }
            finally { method.Parameters[1].OverrideParameterType = null; }
            try
            {
                field.Attributes |= FieldAttributes.InitOnly;
                Assert.That(X64ReferenceFieldStoreProof.Find(method, native), Is.Null,
                    "a readonly field cannot be assigned by this source shape");
            }
            finally { field.Attributes = field.DefaultAttributes; }

            var wrongStore = native.ToArray();
            wrongStore[4].Op1Register = Register.R8;
            Assert.That(X64ReferenceFieldStoreProof.Find(method, wrongStore), Is.Null,
                "the native store must consume the value argument");
            var wrongBarrier = native.ToArray();
            wrongBarrier[6].NearBranch64 = native[7].NearBranchTarget;
            Assert.That(X64ReferenceFieldStoreProof.Find(method, wrongBarrier), Is.Null,
                "the terminal transfer must be the installed write barrier");
            var wrongNull = native.ToArray();
            wrongNull[7].NearBranch64 = native[6].NearBranchTarget;
            Assert.That(X64ReferenceFieldStoreProof.Find(method, wrongNull), Is.Null,
                "the null branch must reach the runtime null helper");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
