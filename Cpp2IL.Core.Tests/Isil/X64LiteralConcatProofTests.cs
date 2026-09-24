using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64LiteralConcatProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerBindsLiteralGetterInheritedFieldAndTailConcat()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_LITERAL_CONCAT_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_LITERAL_CONCAT_FIXTURE_INPUT to the neutral synthetic player-input directory.");
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
            var compose = app.GetAssemblyByName("LiteralConcatFixture")!.Types
                .Single(type => type.Name == "Resolver").Methods
                .Single(method => method.Name == "Compose");
            compose.EnsureRawBytes();
            var native = X86Utils.Iterate(compose).ToArray();
            Assert.That(native.Length, Is.GreaterThanOrEqualTo(20));
            Assert.That(X64LiteralConcatProof.TryProveShape(native.Take(20).ToArray()), Is.True,
                "The authored native body must first match the closed instruction shape.");
            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;
            var region = unwind.ClassifySpan(compose.UnderlyingPointer, compose.UnderlyingPointer + 1);
            Assert.Multiple(() =>
            {
                Assert.That(RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(compose), Is.True,
                    "Compose native binding");
                Assert.That(region.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree),
                    "Compose unwind region");
                Assert.That(unwind.MatchesUnwind(region.Start, region.End, 6, 0,
                    new byte[] { 0x06, 0x32, 0x02, 0x30 }), Is.True,
                    "Compose unwind operations");
                Assert.That(X64MetadataInitializationHelperProof.TryIdentifyStringLiteral(app, pe, unwind,
                    native[6].NearBranchTarget), Is.True, "literal metadata helper");
                Assert.That(X86RuntimeNullThrowProof.TryIdentify(app,
                    native[19].NearBranchTarget), Is.Not.Null, "runtime null helper");
                Assert.That(app.MethodsByAddress.TryGetValue(native[10].NearBranchTarget,
                    out var lookups) && lookups.Any(candidate =>
                    candidate.DeclaringType?.Name == "ResolverBase" &&
                    candidate.Name == "Lookup"), Is.True, "Lookup alias set");
                Assert.That(app.MethodsByAddress.TryGetValue(native[18].NearBranchTarget,
                    out var concats) && concats.Count == 1, Is.True, "Concat binding");
            });
            var slot = native[5].IPRelativeMemoryAddress;
            var flag = native[2].IPRelativeMemoryAddress;
            Assert.Multiple(() =>
            {
                Assert.That(compose.IsVirtual, Is.True, "virtual caller");
                Assert.That(compose.Attributes, Is.EqualTo(compose.DefaultAttributes),
                    "unchanged caller attributes");
                Assert.That(compose.ImplAttributes, Is.EqualTo(compose.DefaultImplAttributes),
                    "unchanged caller implementation attributes");
                Assert.That(compose.Definition?.RawReturnType?.Type,
                    Is.EqualTo(Il2CppTypeEnum.IL2CPP_TYPE_STRING), "raw return type");
                Assert.That(RuntimeNullGuardCoalescer.HasOutputOptions(compose), Is.False,
                    "caller output options");
                Assert.That(region.End - region.Start, Is.InRange(78, 96), "region length");
                Assert.That(native[19].NextIP, Is.LessThanOrEqualTo(region.End),
                    "closed region");
                Assert.That(X64NativePaddingProof.HasInt3Padding(pe, native[19].NextIP,
                    region.End), Is.True, "terminal padding");
                Assert.That(X86CallerExceptionRegionProof.Check(compose, native.Take(20).ToArray(),
                    new HashSet<ulong> { native[19].IP }), Is.Null,
                    "caller exception region");
                Assert.That(unwind.IsWritableZeroInitializedRva((uint)(flag - unwind.ImageBase)),
                    Is.True, "once flag location");
                Assert.That(unwind.IsWritableFileBackedRva((uint)(slot - unwind.ImageBase)),
                    Is.True, "literal slot location");
                Assert.That(app.LibCpp2IlContext.GetLiteralGlobalByAddress(slot)?.IsValid,
                    Is.True, "literal usage validity");
            });
            Assert.That(app.LibCpp2IlContext.GetLiteralGlobalByAddress(slot)?.Type,
                Is.EqualTo(MetadataUsageType.StringLiteral), "literal slot metadata");
            Assert.That(app.LibCpp2IlContext.GetLiteralByAddress(slot), Is.EqualTo("|suffix"),
                "literal metadata value");
            Assert.That(X64LiteralConcatProof.TryBindLookup(app, compose.DeclaringType!, pe,
                unwind, native[10].NearBranchTarget, out var lookup, out var returnedClass),
                Is.True, "typed Lookup alias proof");
            Assert.That(X64LiteralConcatProof.ProveInheritedStringField(returnedClass,
                native[15].MemoryDisplacement64, out _), Is.True,
                "inherited string field proof");
            Assert.That(X64LiteralConcatProof.ProveConcat(app.MethodsByAddress[
                native[18].NearBranchTarget].Single(), app), Is.True, "Concat overload proof");
            var proof = X64LiteralConcatProof.Find(compose, native);
            Assert.That(proof, Is.Not.Null);
            var lifted = X64LiteralConcatProof.TryLift(compose, native);
            Assert.That(lifted, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(proof!.Lookup.Name, Is.EqualTo("Lookup"));
                Assert.That(proof.ValueField.Name, Is.EqualTo("Text"));
                Assert.That(proof.Concat.Name, Is.EqualTo("Concat"));
                Assert.That(proof.Literal, Is.EqualTo("|suffix"));
                Assert.That(lifted!.Select(instruction => instruction.OpCode),
                    Is.EqualTo(new[] { ISIL.OpCode.Call, ISIL.OpCode.Move,
                        ISIL.OpCode.Call, ISIL.OpCode.Return }));
                Assert.That(lifted[0].Operands[0], Is.SameAs(proof.Lookup),
                    "Lookup call target");
                Assert.That(lifted[0].Operands[1], Is.SameAs(lifted[0].Destination),
                    "Lookup result");
                Assert.That(lifted[2].Operands[0], Is.SameAs(proof.Concat),
                    "Concat call target");
                Assert.That(lifted[2].Operands[1], Is.SameAs(lifted[2].Destination),
                    "Concat result");
            });

            var wrongBranch = native.ToArray();
            wrongBranch[12].NearBranch64 = native[18].IP;
            Assert.That(X64LiteralConcatProof.TryProveShape(wrongBranch), Is.False);
            var extraEffect = native.ToArray();
            extraEffect[14].Code = Code.Inc_rm32;
            Assert.That(X64LiteralConcatProof.TryProveShape(extraEffect), Is.False);
            var wrongLookup = native.ToArray();
            wrongLookup[10].NearBranch64 = native[18].NearBranchTarget;
            Assert.That(X64LiteralConcatProof.Find(compose, wrongLookup), Is.Null);
            var wrongLiteralSlot = native.ToArray();
            wrongLiteralSlot[13].MemoryDisplacement64 += 8;
            Assert.That(X64LiteralConcatProof.TryProveShape(wrongLiteralSlot), Is.False);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerBindsProvedClassCastLookupBeforeLiteralConcat()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_RUNTIME_CAST_CONCAT_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_RUNTIME_CAST_CONCAT_FIXTURE_INPUT to the neutral exact player input.");

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
            var assembly = app.GetAssemblyByName("RuntimeCastConcatFixture")!;
            var compose = assembly.Types.Single(type => type.Name == "Resolver").Methods
                .Single(method => method.Name == "Compose");
            var lookup = assembly.Types.Single(type => type.Name == "ResolverBase").Methods
                .Single(method => method.Name == "Lookup");
            compose.EnsureRawBytes();
            lookup.EnsureRawBytes();
            var callerBody = X86Utils.Iterate(compose).TakeWhile(instruction =>
                instruction.Code != Code.Int3).ToArray();
            var calleeBody = X86Utils.Iterate(lookup).TakeWhile(instruction =>
                instruction.Code != Code.Int3).ToArray();
            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;
            var castShape = X64ClassCastLookupProof.TryProveShape(calleeBody);
            Assert.That(castShape, Is.Not.Null);
            Assert.That(X64ClassCastLookupProof.Find(lookup, calleeBody), Is.Not.Null);
            Assert.That(X64LiteralConcatProof.TryProveShape(callerBody), Is.True);
            Assert.That(X64LiteralConcatProof.TryBindLookup(app, compose.DeclaringType!, pe,
                unwind, callerBody[10].NearBranchTarget, out var boundLookup,
                out var returnedClass), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(boundLookup, Is.SameAs(lookup));
                Assert.That(returnedClass, Is.SameAs(lookup.ReturnType));
                Assert.That(X64LiteralConcatProof.ProveInheritedStringField(returnedClass,
                    callerBody[15].MemoryDisplacement64, out _), Is.True);
            });

            var proof = X64LiteralConcatProof.Find(compose, callerBody);
            var lifted = X64LiteralConcatProof.TryLift(compose, callerBody);
            Assert.Multiple(() =>
            {
                Assert.That(proof, Is.Not.Null);
                Assert.That(proof!.Lookup, Is.SameAs(lookup));
                Assert.That(proof.ValueField.Name, Is.EqualTo("Text"));
                Assert.That(proof.Literal, Is.EqualTo("|tag"));
                Assert.That(lifted, Is.Not.Null);
                Assert.That(lifted![0].Operands[0], Is.SameAs(lookup));
            });

            Assert.That(X64ClassCastLookupProof.BindProvedShape(lookup,
                castShape! with { Initializer = callerBody[10].NearBranchTarget }),
                Is.Null, "An unrelated call target cannot authenticate the cast initializer.");

            var rawLookup = lookup.Definition!;
            var originalFlags = rawLookup.flags;
            try
            {
                rawLookup.flags = (ushort)(((MethodAttributes)originalFlags &
                    ~MethodAttributes.MemberAccessMask) | MethodAttributes.Private);
                Assert.That(X64ClassCastLookupProof.Find(lookup, calleeBody), Is.Not.Null,
                    "The independent native cast proof does not require method visibility.");
                Assert.That(X64LiteralConcatProof.TryBindLookup(app, compose.DeclaringType!,
                    pe, unwind, callerBody[10].NearBranchTarget, out _, out _), Is.False,
                    "Generated source cannot call a private method on the base type.");
            }
            finally { rawLookup.flags = originalFlags; }

            var hiddenMethod = compose.DeclaringType!.Methods.Single(method =>
                method.Name == ".ctor");
            var originalName = hiddenMethod.OverrideName;
            try
            {
                hiddenMethod.OverrideName = lookup.Name;
                Assert.That(X64ClassCastLookupProof.Find(lookup, calleeBody), Is.Not.Null,
                    "The callee's independent cast evidence is unchanged.");
                Assert.That(X64LiteralConcatProof.TryBindLookup(app, compose.DeclaringType,
                    pe, unwind, callerBody[10].NearBranchTarget, out _, out _), Is.False,
                    "An inherited call can bind differently when the caller hides its name.");
            }
            finally { hiddenMethod.OverrideName = originalName; }

            var aliases = app.MethodsByAddress[lookup.UnderlyingPointer];
            aliases.Add(lookup);
            try
            {
                Assert.That(X64LiteralConcatProof.TryBindLookup(app, compose.DeclaringType!,
                    pe, unwind, callerBody[10].NearBranchTarget, out _, out _), Is.False,
                    "The cast callee must have one native method binding.");
            }
            finally { aliases.RemoveAt(aliases.Count - 1); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
