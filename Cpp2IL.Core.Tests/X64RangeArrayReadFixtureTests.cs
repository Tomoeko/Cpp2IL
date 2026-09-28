using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64RangeArrayReadFixtureTests
{
    [Test]
    public void ExactPlayerBindsPublicRangeAndRejectsNearbyShapes()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_RANGE_ARRAY_READ_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_RANGE_ARRAY_READ_FIXTURE_INPUT to the neutral exact player input.");
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
            var owner = app.GetAssemblyByName("RangeArrayReadFixture")!.Types
                .Single(type => type.Name == "RangeArrayOwner");
            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;

            foreach (var (methodName, fieldName) in new[]
                     { ("ReadWord", "Words"), ("ReadTag", "Tags") })
            {
                var method = owner.Methods.Single(candidate =>
                    candidate.Name == methodName);
                var evidence = X64RangeArrayReadProof.Find(method);
                Assert.That(evidence, Is.Not.Null, methodName);
                Assert.Multiple(() =>
                {
                    Assert.That(evidence!.ArrayField.Name, Is.EqualTo(fieldName));
                    Assert.That(evidence.Range.Name, Is.EqualTo("Range"));
                    Assert.That(evidence.Range.DeclaringType!.FullName,
                        Is.EqualTo("UnityEngine.Random"));
                    Assert.That(evidence.Range.DeclaringType.DeclaringAssembly.Name,
                        Is.EqualTo("UnityEngine.CoreModule"));
                    Assert.That(evidence.Range.UnderlyingPointer, Is.Not.Zero);
                });
                var aliases = app.MethodsByAddress[evidence!.Range.UnderlyingPointer];
                Assert.That(aliases.Select(alias => alias.Name),
                    Is.EquivalentTo(new[] { "Range", "RandomRangeInt" }));

                var decoded = X86Utils.Iterate(method).ToArray();
                Assert.That(X64ArrayGuardSiteProof.TryCompleteFileBackedRegion(
                    method, decoded, pe, unwind, out var body), Is.True);
                Assert.That(body.Count, Is.EqualTo(20));
                Assert.That(X64RangeArrayReadProof.TryProveShape(body, pe),
                    Is.Not.Null);
            }

            var selected = owner.Methods.Single(method => method.Name == "ReadWord");
            Assert.That(X64ArrayGuardSiteProof.TryCompleteFileBackedRegion(
                selected, X86Utils.Iterate(selected).ToArray(), pe, unwind,
                out var native), Is.True);

            // Both target compiler schedules preserve the CMP/JAE flags and
            // sign-extend the same EAX index before the element address.
            var afterBranch = native.ToArray();
            var start = afterBranch[9].IP;
            (afterBranch[9], afterBranch[10], afterBranch[11]) =
                (afterBranch[10], afterBranch[11], afterBranch[9]);
            for (var index = 9; index <= 11; index++)
            {
                afterBranch[index].IP = start;
                start = afterBranch[index].NextIP;
            }
            Assert.That(X64RangeArrayReadProof.TryProveShape(afterBranch, pe),
                Is.Not.Null, "the second exact CDQE schedule");

            foreach (var mutation in new[]
                     { "field origin", "null arm", "length base", "length destination",
                         "lower bound", "hidden argument", "index producer",
                         "signed bounds", "bounds arm", "extension", "element base",
                         "element index", "element stride", "element offset",
                         "missing trap", "extra instruction" })
            {
                var changed = native.ToArray();
                switch (mutation)
                {
                    case "field origin": changed[2].MemoryBase = Register.RDX; break;
                    case "null arm": changed[4].NearBranch64 = changed[18].IP; break;
                    case "length base": changed[5].MemoryBase = Register.RCX; break;
                    case "length destination": changed[5].Op0Register = Register.EAX; break;
                    case "lower bound": changed[7].Op0Register = Register.EDX; break;
                    case "hidden argument": changed[6].Op0Register = Register.EDX; break;
                    case "index producer": changed[8].Code = Code.Jmp_rel32_64; break;
                    case "signed bounds": changed[11].Code = Code.Jge_rel8_64; break;
                    case "bounds arm": changed[11].NearBranch64 = changed[16].IP; break;
                    case "extension": changed[9].Code = Code.Cwde; break;
                    case "element base": changed[12].MemoryBase = Register.RCX; break;
                    case "element index": changed[12].MemoryIndex = Register.RDX; break;
                    case "element stride": changed[12].MemoryIndexScale = 4; break;
                    case "element offset": changed[12].MemoryDisplacement64++; break;
                    case "missing trap": changed[19].Code = Code.Nopd; break;
                    case "extra instruction": changed = [.. changed, changed[^1]]; break;
                }
                Assert.That(X64RangeArrayReadProof.TryProveShape(changed, pe),
                    Is.Null, mutation);
            }

            var words = owner.Fields.Single(field => field.Name == "Words");
            try
            {
                words.OverrideOffset = words.DefaultOffset + 8;
                Assert.That(X64RangeArrayReadProof.Find(selected), Is.Null);
            }
            finally { words.OverrideOffset = null; }

            var range = X64RangeArrayReadProof.Find(selected)!.Range;
            try
            {
                range.DeclaringType!.OverrideAttributes =
                    (range.DeclaringType.DefaultAttributes &
                        ~TypeAttributes.VisibilityMask) | TypeAttributes.NotPublic;
                Assert.That(X64RangeArrayReadProof.Find(selected), Is.Null,
                    "a cross-assembly call needs a public declaring type");
            }
            finally { range.DeclaringType!.OverrideAttributes = null; }

            try
            {
                app.MethodsByAddress[range.UnderlyingPointer].Add(range);
                Assert.That(X64RangeArrayReadProof.Find(selected), Is.Null,
                    "a third alias invalidates the public-call binding");
            }
            finally { app.MethodsByAddress[range.UnderlyingPointer].RemoveAt(2); }

            var interior = selected.UnderlyingPointer + 5;
            Assert.That(app.MethodsByAddress.ContainsKey(interior), Is.False);
            try
            {
                app.MethodsByAddress.Add(interior, [selected]);
                Assert.That(X64RangeArrayReadProof.Find(selected), Is.Null,
                    "a second managed entry invalidates the whole native region");
            }
            finally { app.MethodsByAddress.Remove(interior); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
