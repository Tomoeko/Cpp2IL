using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64FixedBooleanConjunctionProofTests
{
    [Test]
    public void ExactPlayerRequiresOrderedReadsAndUniqueMetadataBindings()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_FIXED_BOOLEAN_CONJUNCTION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FIXED_BOOLEAN_CONJUNCTION_FIXTURE_INPUT to the neutral player input.");

        var binary = Path.Combine(directory, "GameAssembly.dll");
        var metadata = Path.Combine(directory, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("FixedBooleanConjunctionFixture")!.Types
                .Single(type => type.Name == "BooleanPair");
            var method = owner.Methods.Single(candidate => candidate.Name == "BothFalseAtZero");
            var pe = (PE)app.Binary;
            var body = X64Stack28BodyProof.Read(method, 23, 96);
            Assert.That(body, Is.Not.Null);
            var shape = X64FixedBooleanConjunctionProof.TryProveShape(body!, pe);
            Assert.That(shape, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(shape!.FirstOffset,
                    Is.EqualTo(owner.Fields.Single(field => field.Name == "First").Offset));
                Assert.That(shape.SecondOffset,
                    Is.EqualTo(owner.Fields.Single(field => field.Name == "Second").Offset));
                Assert.That(X64FixedBooleanConjunctionProof.Find(method)?.First.Name,
                    Is.EqualTo("First"));
                Assert.That(X64FixedBooleanConjunctionProof.Find(method)?.Second.Name,
                    Is.EqualTo("Second"));
            });

            foreach (var mutation in new[]
                     {
                         "first null exit", "first signed bounds", "first bounds exit",
                         "first byte width", "first predicate", "early return value",
                         "second read entry", "second field alias", "second null exit",
                         "second length base", "second signed bounds", "second bounds exit",
                         "second byte width", "second predicate", "missing trap",
                         "return replaced", "extra instruction"
                     })
            {
                var changed = body!.ToArray();
                switch (mutation)
                {
                    case "first null exit": changed[3].NearBranch64 = changed[22].IP; break;
                    case "first signed bounds": changed[5].Code = Code.Jge_rel8_64; break;
                    case "first bounds exit": changed[5].NearBranch64 = changed[20].IP; break;
                    case "first byte width": changed[6].Code = Code.Cmp_rm32_imm8; break;
                    case "first predicate": changed[7].Code = Code.Jne_rel8_64; break;
                    case "early return value": changed[8].Code = Code.Or_r8_rm8; break;
                    case "second read entry": changed[7].NearBranch64 = changed[12].IP; break;
                    case "second field alias":
                        changed[11].MemoryDisplacement64 = changed[1].MemoryDisplacement64;
                        break;
                    case "second null exit": changed[13].NearBranch64 = changed[22].IP; break;
                    case "second length base": changed[14].MemoryBase = Register.RCX; break;
                    case "second signed bounds": changed[15].Code = Code.Jge_rel8_64; break;
                    case "second bounds exit": changed[15].NearBranch64 = changed[20].IP; break;
                    case "second byte width": changed[16].Code = Code.Cmp_rm32_imm8; break;
                    case "second predicate": changed[17].Code = Code.Setne_rm8; break;
                    case "missing trap": changed[21].Code = Code.Nopd; break;
                    case "return replaced": changed[19].Code = Code.Jmp_rel8_64; break;
                    case "extra instruction": changed = [..changed, changed[^1]]; break;
                }
                Assert.That(X64FixedBooleanConjunctionProof.TryProveShape(changed, pe),
                    Is.Null, mutation);
            }

            var sentinel = owner.Fields.Single(field => field.Name == "Sentinel");
            try
            {
                sentinel.OverrideOffset = shape!.SecondOffset;
                Assert.That(X64FixedBooleanConjunctionProof.Find(method), Is.Null,
                    "an overlapping field makes the native second field ambiguous");
            }
            finally { sentinel.OverrideOffset = null; }

            try
            {
                method.OverrideReturnType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64FixedBooleanConjunctionProof.Find(method), Is.Null,
                    "the native byte return requires an unchanged Boolean signature");
            }
            finally { method.OverrideReturnType = null; }

            var aliases = app.MethodsByAddress[method.UnderlyingPointer];
            aliases.Add(method);
            try
            {
                Assert.That(X64FixedBooleanConjunctionProof.Find(method), Is.Null,
                    "duplicate managed bindings do not identify one method");
            }
            finally { aliases.RemoveAt(aliases.Count - 1); }

            var interior = method.UnderlyingPointer + 5;
            Assert.That(app.MethodsByAddress.ContainsKey(interior), Is.False);
            try
            {
                app.MethodsByAddress.Add(interior, [method]);
                Assert.That(X64FixedBooleanConjunctionProof.Find(method), Is.Null,
                    "an interior managed entry invalidates the complete native root");
            }
            finally { app.MethodsByAddress.Remove(interior); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
