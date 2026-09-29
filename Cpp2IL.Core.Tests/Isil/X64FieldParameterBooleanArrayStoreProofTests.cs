using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64FieldParameterBooleanArrayStoreProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerBindsFieldIndexValueAndBothExceptionalExits()
    {
        var input = Environment.GetEnvironmentVariable(
            "CPP2IL_FIELD_PARAMETER_BOOLEAN_ARRAY_STORE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_FIELD_PARAMETER_BOOLEAN_ARRAY_STORE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(input!, "GameAssembly.dll");
        var metadata = Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("FieldParameterBooleanArrayStoreFixture")!
                .Types.Single(type => type.Name == "BooleanArrayOwner");
            var method = owner.Methods.Single(candidate => candidate.Name == "SetAt");
            var field = owner.Fields.Single(candidate => candidate.Name == "Values");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            var pe = (PE)app.Binary;
            Assert.Multiple(() =>
            {
                Assert.That(method.RawBytes.Length, Is.EqualTo(43));
                Assert.That(native, Has.Length.EqualTo(13));
                Assert.That(X64FieldParameterBooleanArrayStoreProof
                    .TryProveShape(native, pe)?.FieldOffset, Is.EqualTo(field.Offset));
                Assert.That(X64FieldParameterBooleanArrayStoreProof.Find(method)?.ArrayField,
                    Is.SameAs(field));
                Assert.That(native[3].NearBranchTarget, Is.EqualTo(native[10].IP));
                Assert.That(native[5].NearBranchTarget, Is.EqualTo(native[12].IP));
                Assert.That(native[7].Op1Register, Is.EqualTo(Register.R8L));
            });

            foreach (var change in new[]
            {
                (Index: 3, Change: (Action<Instruction[]>)(body => body[3].NearBranch64 = body[12].IP)),
                (Index: 5, Change: (Action<Instruction[]>)(body => body[5].NearBranch64 = body[10].IP)),
                (Index: 5, Change: (Action<Instruction[]>)(body => body[5].Code = Code.Jge_rel8_64)),
                (Index: 6, Change: (Action<Instruction[]>)(body => body[6].Op1Register = Register.ECX)),
                (Index: 7, Change: (Action<Instruction[]>)(body => body[7].Op1Register = Register.DL)),
                (Index: 7, Change: (Action<Instruction[]>)(body => body[7].MemoryDisplacement64++)),
                (Index: 7, Change: (Action<Instruction[]>)(body => body[7].Code = Code.Mov_rm32_r32)),
                (Index: 7, Change: (Action<Instruction[]>)(body => body[7].HasLockPrefix = true)),
                (Index: 11, Change: (Action<Instruction[]>)(body => body[11].Code = Code.Nopd)),
            })
            {
                var changed = native.ToArray();
                change.Change(changed);
                Assert.That(X64FieldParameterBooleanArrayStoreProof.TryProveShape(changed, pe),
                    Is.Null, $"native instruction {change.Index} mutation");
            }

            var originalOffset = field.Offset;
            try
            {
                field.Offset += 8;
                Assert.That(X64FieldParameterBooleanArrayStoreProof.Find(method), Is.Null,
                    "the field's original layout must match the native displacement");
            }
            finally { field.Offset = originalOffset; }

            field.OverrideFieldType = app.SystemTypes.SystemObjectType;
            try
            {
                Assert.That(X64FieldParameterBooleanArrayStoreProof.Find(method), Is.Null,
                    "the array type cannot be replaced after analysis");
            }
            finally { field.OverrideFieldType = null; }

            var rawField = field.BackingData!.Field.RawFieldType!;
            var fieldKind = rawField.Type;
            try
            {
                rawField.Type = Il2CppTypeEnum.IL2CPP_TYPE_I8;
                Assert.That(X64FieldParameterBooleanArrayStoreProof.Find(method), Is.Null,
                    "a changed original field descriptor invalidates the proof");
            }
            finally { rawField.Type = fieldKind; }

            var rawValue = method.Parameters[1].Definition!.RawType!;
            var valueKind = rawValue.Type;
            try
            {
                rawValue.Type = Il2CppTypeEnum.IL2CPP_TYPE_I4;
                Assert.That(X64FieldParameterBooleanArrayStoreProof.Find(method), Is.Null,
                    "the stored byte must originate from the original Boolean parameter");
            }
            finally { rawValue.Type = valueKind; }

            var rawReturn = method.Definition!.RawReturnType!;
            var returnKind = rawReturn.Type;
            try
            {
                rawReturn.Type = Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN;
                Assert.That(X64FieldParameterBooleanArrayStoreProof.Find(method), Is.Null);
            }
            finally { rawReturn.Type = returnKind; }

            var changedBinary = File.ReadAllBytes(binary);
            var branchEnd = checked((int)pe.MapVirtualAddressToRaw(
                native[3].NextIP - 1, false));
            changedBinary[branchEnd] ^= 1;
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changedBinary, File.ReadAllBytes(metadata),
                UnityVersion.Parse("2021.3.35f1"));
            var changedMethod = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("FieldParameterBooleanArrayStoreFixture")!
                .Types.Single(type => type.Name == "BooleanArrayOwner")
                .Methods.Single(candidate => candidate.Name == "SetAt");
            Assert.That(X64FieldParameterBooleanArrayStoreProof.Find(changedMethod), Is.Null,
                "a changed original null branch cannot authorize recovery");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
