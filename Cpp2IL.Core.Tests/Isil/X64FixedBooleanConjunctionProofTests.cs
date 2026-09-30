using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
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
            var pe = (PE)app.Binary;
            foreach (var (name, index) in new[] { ("BothFalseAtZero", 0), ("BothFalseAtOne", 1) })
            {
                var candidate = owner.Methods.Single(method => method.Name == name);
                var native = X64Stack28BodyProof.Read(candidate, 23, 96);
                Assert.That(native, Is.Not.Null, name);
                var candidateShape = X64FixedBooleanConjunctionProof.TryProveShape(native!, pe);
                var evidence = X64FixedBooleanConjunctionProof.Find(candidate);
                Assert.That(candidateShape, Is.Not.Null, name);
                Assert.Multiple(() =>
                {
                    Assert.That(candidateShape!.FirstOffset,
                        Is.EqualTo(owner.Fields.Single(field => field.Name == "First").Offset));
                    Assert.That(candidateShape.SecondOffset,
                        Is.EqualTo(owner.Fields.Single(field => field.Name == "Second").Offset));
                    Assert.That(candidateShape.FirstIndex, Is.EqualTo(index));
                    Assert.That(candidateShape.SecondIndex, Is.EqualTo(index));
                    Assert.That(evidence?.First.Name, Is.EqualTo("First"));
                    Assert.That(evidence?.Second.Name, Is.EqualTo("Second"));
                    Assert.That(evidence?.FirstIndex, Is.EqualTo(index));
                    Assert.That(evidence?.SecondIndex, Is.EqualTo(index));
                });
            }

            var method = owner.Methods.Single(candidate => candidate.Name == "BothFalseAtZero");
            var body = X64Stack28BodyProof.Read(method, 23, 96)!;
            var shape = X64FixedBooleanConjunctionProof.TryProveShape(body, pe)!;
            var secondRead = body[7].Code == Code.Jne_rel8_64 ? 8 : 11;
            var falseReturn = secondRead == 8 ? 17 : 8;

            Assert.That(owner.BaseType, Is.TypeOf<GenericInstanceTypeAnalysisContext>());
            var constructed = (GenericInstanceTypeAnalysisContext)owner.BaseType!;
            var rawBase = constructed.OriginalRawType!;
            Assert.That(constructed.HasUnchangedOriginalRawType, Is.True);
            foreach (var mutation in new[] { "modifiers", "pinned", "bits", "data" })
            {
                var mods = rawBase.NumMods;
                var pinned = rawBase.Pinned;
                var bits = rawBase.Bits;
                var data = rawBase.Data.Dummy;
                try
                {
                    switch (mutation)
                    {
                        case "modifiers": rawBase.NumMods = 1; break;
                        case "pinned": rawBase.Pinned = 1; break;
                        case "bits": rawBase.Bits ^= 1; break;
                        case "data": rawBase.Data.Dummy ^= 1; break;
                    }
                    Assert.That(X64FixedBooleanConjunctionProof.Find(method), Is.Null,
                        "the cached generic base cannot authenticate changed raw " + mutation);
                }
                finally
                {
                    rawBase.NumMods = mods;
                    rawBase.Pinned = pinned;
                    rawBase.Bits = bits;
                    rawBase.Data.Dummy = data;
                }
                Assert.That(X64FixedBooleanConjunctionProof.Find(method), Is.Not.Null);
            }
            var injected = new InjectedFieldAnalysisContext("Blocked", app.SystemTypes.SystemInt32Type,
                FieldAttributes.Public, constructed.GenericType, 16);
            try
            {
                constructed.GenericType.Fields.Add(injected);
                Assert.That(X64FixedBooleanConjunctionProof.Find(method), Is.Null,
                    "the constructed ancestor must remain fieldless");
                injected.OverrideAttributes = FieldAttributes.Public | FieldAttributes.Static;
                Assert.That(X64FixedBooleanConjunctionProof.Find(method), Is.Null,
                    "changing an instance field to static cannot remove its original storage evidence");
            }
            finally
            {
                injected.OverrideAttributes = null;
                constructed.GenericType.Fields.Remove(injected);
            }

            var baseDefinition = constructed.GenericType.Definition!;
            var originalBitfield = baseDefinition.Bitfield;
            try
            {
                baseDefinition.Bitfield |= 1u << 3;
                Assert.That(X64FixedBooleanConjunctionProof.Find(method), Is.Null,
                    "a changed ancestor initializer invalidates the bounded layout admission");
            }
            finally { baseDefinition.Bitfield = originalBitfield; }
            Assert.That(X64FixedBooleanConjunctionProof.Find(method), Is.Not.Null);

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
                    case "first predicate":
                        changed[7].Code = secondRead == 8 ? Code.Je_rel8_64 : Code.Jne_rel8_64;
                        break;
                    case "early return value": changed[falseReturn].Code = Code.Or_r8_rm8; break;
                    case "second read entry": changed[7].NearBranch64 = changed[secondRead + 1].IP; break;
                    case "second field alias":
                        changed[secondRead].MemoryDisplacement64 = changed[1].MemoryDisplacement64;
                        break;
                    case "second null exit": changed[secondRead + 2].NearBranch64 = changed[22].IP; break;
                    case "second length base": changed[secondRead + 3].MemoryBase = Register.RCX; break;
                    case "second signed bounds": changed[secondRead + 4].Code = Code.Jge_rel8_64; break;
                    case "second bounds exit": changed[secondRead + 4].NearBranch64 = changed[20].IP; break;
                    case "second byte width": changed[secondRead + 5].Code = Code.Cmp_rm32_imm8; break;
                    case "second predicate": changed[secondRead + 6].Code = Code.Setne_rm8; break;
                    case "missing trap": changed[21].Code = Code.Nopd; break;
                    case "return replaced": changed[secondRead + 8].Code = Code.Jmp_rel8_64; break;
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
