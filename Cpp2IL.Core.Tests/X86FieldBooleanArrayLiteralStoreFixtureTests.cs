using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using IsilOpCode = Cpp2IL.Core.ISIL.OpCode;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X86FieldBooleanArrayLiteralStoreFixtureTests
{
    [TestCase("SetTrue", true)]
    [TestCase("SetFalse", false)]
    public void ExactStoreRequiresBooleanElementAndBothProvedExits(string name, bool value)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_FIELD_BOOLEAN_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FIELD_BOOLEAN_ARRAY_FIXTURE_INPUT to the neutral player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("BooleanFieldArrayFixture")!.Types
                .Single(type => type.Name == "BooleanArrayState");
            var method = owner.Methods.Single(candidate => candidate.Name == name);
            var native = X86Utils.Iterate(method).ToArray();
            var evidence = X86FieldBooleanArrayLiteralStoreProof.Find(method, native);
            Assert.That(evidence?.Value, Is.EqualTo(value));
            Assert.That(evidence?.ArrayField, Is.SameAs(owner.Fields.Single(field => field.Name == "Values")));
            Assert.That(evidence!.ArrayField.FieldType, Is.TypeOf<SzArrayTypeAnalysisContext>());
            Assert.That(((SzArrayTypeAnalysisContext)evidence.ArrayField.FieldType).ElementType,
                Is.SameAs(app.SystemTypes.SystemBooleanType));
            Assert.That(app.InstructionSet.GetIsilFromMethod(method).Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { IsilOpCode.Move, IsilOpCode.Move, IsilOpCode.Return }));

            method.Analyze();
            var recovered = method.ControlFlowGraph!.Instructions.ToArray();
            Assert.That(recovered.Any(instruction => instruction is
                { OpCode: IsilOpCode.Move, Operands: [_, Cpp2IL.Core.ISIL.FieldReference field] } &&
                ReferenceEquals(field.Field, evidence.ArrayField)), Is.True);
            Assert.That(recovered.Any(instruction => instruction is
                { OpCode: IsilOpCode.Move,
                    Operands: [Cpp2IL.Core.ISIL.ArrayAccess { Index: Cpp2IL.Core.ISIL.LocalVariable },
                        Cpp2IL.Core.ISIL.Immediate literal] } &&
                literal.Value == (value ? 1 : 0)), Is.True);

            var wrongField = native.ToArray();
            wrongField[1].MemoryDisplacement64 += 8;
            Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(method, wrongField), Is.Null);
            var wrongNull = native.ToArray();
            wrongNull[3].NearBranch64 = native[12].IP;
            Assert.That(X86FieldBooleanArrayLiteralStoreProof.TryProveShape(wrongNull,
                (LibCpp2IL.PE.PE)app.Binary), Is.Null);
            var wrongBounds = native.ToArray();
            wrongBounds[5].Code = Code.Ja_rel8_64;
            Assert.That(X86FieldBooleanArrayLiteralStoreProof.TryProveShape(wrongBounds,
                (LibCpp2IL.PE.PE)app.Binary), Is.Null);
            var wrongElement = native.ToArray();
            wrongElement[7].MemoryDisplacement64++;
            Assert.That(X86FieldBooleanArrayLiteralStoreProof.TryProveShape(wrongElement,
                (LibCpp2IL.PE.PE)app.Binary), Is.Null);
            var wrongValue = native.ToArray();
            wrongValue[7].Immediate8 = 2;
            Assert.That(X86FieldBooleanArrayLiteralStoreProof.TryProveShape(wrongValue,
                (LibCpp2IL.PE.PE)app.Binary), Is.Null);

            try
            {
                evidence.ArrayField.OverrideOffset = evidence.ArrayField.DefaultOffset + 8;
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(method, native), Is.Null);
            }
            finally { evidence.ArrayField.OverrideOffset = null; }
            try
            {
                evidence.ArrayField.OverrideFieldType = app.SystemTypes.SystemInt32Type;
                Assert.That(X86FieldBooleanArrayLiteralStoreProof.Find(method, native), Is.Null);
            }
            finally { evidence.ArrayField.OverrideFieldType = null; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
