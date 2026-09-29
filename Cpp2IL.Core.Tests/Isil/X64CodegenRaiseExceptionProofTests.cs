using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64CodegenRaiseExceptionProofTests
{
    [Test]
    public void ClearsBothTraceReferencesThenRaisesTheOriginalExceptionAndFrame()
        => Assert.That(new NativeFixture().Proves(), Is.True);

    [TestCase(0x1000UL, 0, "4889742408")]
    [TestCase(0x1000UL, 1, "56")]
    [TestCase(0x1000UL, 2, "4883EC28")]
    [TestCase(0x1000UL, 3, "488BFA")]
    [TestCase(0x1000UL, 4, "488BD9")]
    [TestCase(0x1000UL, 5, "4883C130")]
    [TestCase(0x1000UL, 6, "E800000000")]
    [TestCase(0x1000UL, 7, "488D4F48")]
    [TestCase(0x1000UL, 7, "488D4B40")]
    [TestCase(0x1000UL, 8, "E800000000")]
    [TestCase(0x1000UL, 9, "488BD7")]
    [TestCase(0x1000UL, 10, "488BCB")]
    [TestCase(0x1000UL, 11, "E800000000")]
    [TestCase(0x2000UL, 0, "C70100000000")]
    [TestCase(0x2000UL, 0, "48C70101000000")]
    [TestCase(0x2000UL, 0, "48C7410800000000")]
    [TestCase(0x2000UL, 1, "C20000")]
    [TestCase(0x3000UL, 0, "4883EC20")]
    [TestCase(0x3000UL, 1, "33C9")]
    [TestCase(0x3000UL, 1, "BA01000000")]
    [TestCase(0x3000UL, 2, "FF11")]
    public void ChangedStateStoresDataflowWidthAndRaiseTargetRemainUnproved(
        ulong address, int index, string replacement)
    {
        var fixture = new NativeFixture();
        fixture.Replace(address, index, replacement);
        Assert.That(fixture.Proves(), Is.False);
    }

    [TestCase(0x1000UL)]
    [TestCase(0x2000UL)]
    [TestCase(0x3000UL)]
    public void EveryExecutedCandidateFrameRequiresIndependentUnwindEvidence(ulong address)
    {
        var fixture = new NativeFixture();
        fixture.Unwind = regions => regions.All(region => region.Start != address);
        Assert.That(fixture.Proves(), Is.False);
    }

    [TestCase(0x1000UL, 0)]
    [TestCase(0x1000UL, 1)]
    [TestCase(0x1000UL, 2)]
    [TestCase(0x3000UL, 0)]
    public void PrologInstructionBoundariesMustMatchTheUnwindOffsets(ulong target, int changedIndex)
    {
        var fixture = new NativeFixture();
        Assert.That(X64CodegenRaiseExceptionProof.TryProve(0x1000, (address, count) =>
        {
            var body = fixture.Read(address, count)?.ToArray();
            if (body != null && address == target)
            {
                var changed = body[changedIndex];
                changed.Length++;
                body[changedIndex] = changed;
                for (var index = changedIndex + 1; index < body.Length; index++)
                {
                    changed = body[index];
                    changed.IP++;
                    body[index] = changed;
                }
            }
            return body;
        }, _ => fixture.ExportAddress, _ => true), Is.False);
    }

    [Test]
    public void MissingExportTruncatedDecodeAndAddedPrefixesFailClosed()
    {
        var fixture = new NativeFixture { ExportAddress = 0 };
        Assert.That(fixture.Proves(), Is.False);
        fixture.ExportAddress = 0x3000;
        fixture.Replace(0x2000, 0, "6448C70100000000");
        Assert.That(fixture.Proves(), Is.False);
        fixture = new NativeFixture();
        fixture.Bodies[0x1000] = fixture.Bodies[0x1000][..^1];
        Assert.That(fixture.Proves(), Is.False);
        fixture = new NativeFixture();
        Assert.That(X64CodegenRaiseExceptionProof.TryProve(0x1000, (address, count) =>
        {
            var body = fixture.Read(address, count)?.ToArray();
            if (body != null && address == 0x1000)
            {
                var changed = body[7];
                changed.IP++;
                body[7] = changed;
            }
            return body;
        }, _ => fixture.ExportAddress, _ => true), Is.False);
    }

    [Test]
    [NonParallelizable]
    public void ExactClassCastPlayerBindsOnlyTheFinalRaiseComponentAndUnchangedTraceLayout()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_EXPLICIT_CLASS_CAST_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_EXPLICIT_CLASS_CAST_FIXTURE_INPUT to the synthetic exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var cast = app.GetAssemblyByName("ExplicitClassCastFixture")!.Types
                .SelectMany(type => type.Methods).Single(method => method.Name == "Cast");
            cast.EnsureRawBytes();
            var native = X86Utils.Iterate(cast).ToArray();
            var region = X64UnwindProof.ForApplication(app)!.ClassifySpan(
                cast.UnderlyingPointer, cast.UnderlyingPointer + 1);
            var helper = native.Last(instruction => instruction.IP < region.End &&
                instruction.Code == Code.Call_rel32_64).NearBranchTarget;
            // The helper itself is not the proved final raiser. Its formatter,
            // string ownership and EH actions retain separate obligations.
            Assert.That(X64CodegenRaiseExceptionProof.TryIdentify(app, helper), Is.False);
            var handler = X64UnwindProof.ForApplication(app)!.GetHandler(helper);
            Assert.That(handler, Is.Not.Null, "The native cast helper carries separate ownership cleanup.");
            var cleanup = X64Eh4MapProof.ParseCleanup(app.Binary.GetRawBinaryContent(),
                X64UnwindProof.ForApplication(app)!, handler!.Value);
            Assert.That(cleanup, Is.Not.Null);
            Assert.That(cleanup!.UnwindActions.Select(action => action.Kind), Is.EqualTo(new byte[] { 1, 0 }));
            Assert.That(cleanup.UnwindActions[0].ObjectOffset, Is.EqualTo(0x20));
            Assert.That(cleanup.TryBlocks, Is.Empty);
            var offset = checked((int)app.Binary.MapVirtualAddressToRaw(helper));
            var length = checked((int)(handler!.Value.End - helper));
            var reader = X86Utils.Disassemble(app.Binary.GetRawBinaryContent().Slice(offset, length),
                helper, false).ToArray();
            var finalRaise = reader.Last(instruction => instruction.Code == Code.Call_rel32_64)
                .NearBranchTarget;
            Assert.That(X64CodegenRaiseExceptionProof.TryIdentify(app, finalRaise), Is.True);
            var type = app.SystemTypes.SystemExceptionType;
            var field = type.Fields.Single(candidate => candidate.Name == "_stackTrace");
            var fieldOffset = field.Offset;
            var attributes = field.Attributes;
            void Reject(Action mutate, Action restore)
            {
                mutate();
                Assert.That(X64CodegenRaiseExceptionProof.TryIdentify(app, finalRaise), Is.False);
                restore();
                Assert.That(X64CodegenRaiseExceptionProof.TryIdentify(app, finalRaise), Is.True);
            }
            Reject(() => field.Offset += 8, () => field.OverrideOffset = null);
            Reject(() => field.Name = "other_trace", () => field.OverrideName = null);
            Reject(() => field.Attributes |= FieldAttributes.Static, () => field.Attributes = attributes);
            Reject(() => field.FieldType = app.SystemTypes.SystemStringType, () => field.OverrideFieldType = null);
            Reject(() => type.Fields.Add(field), () => type.Fields.RemoveAt(type.Fields.Count - 1));
            Assert.That(field.Offset, Is.EqualTo(fieldOffset));
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    [NonParallelizable]
    public void ManagedCatchProofRejectsChangedPrepareEffectsAndLoaderRelocations()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_EXCEPTION_REGION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_EXCEPTION_REGION_FIXTURE_INPUT to the synthetic exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var method = app.GetAssemblyByName("ExceptionRegionFixture")!.Types
                .SelectMany(type => type.Methods).Single(candidate => candidate.Name == "CatchZero");
            var proof = X64CatchDivideBodyProof.Find(method);
            Assert.That(proof, Is.Not.Null);
            Assert.That(X64ManagedThrowHelperProof.Check(method, proof!), Is.True);
            Assert.That(X64CatchDivideRecovery.TryGenerate(method,
                method.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!), Is.True);
            var pe = (PE)app.Binary;
            var raiser = X64NativeInstructionReader.Read(pe, X64UnwindProof.ForApplication(app)!,
                proof!.Raiser, 12, 96)!;
            var prepare = raiser[6].NearBranchTarget;
            Assert.That(prepare, Is.EqualTo(raiser[8].NearBranchTarget));
            Assert.That(X64TerminalManagedThrowProof.ProveRaiseWrapper(app, pe,
                X64UnwindProof.ForApplication(app)!, proof.Raiser), Is.True);
            var image = File.ReadAllBytes(binary);
            var setter = X64NativeInstructionReader.Read(pe, X64UnwindProof.ForApplication(app)!, prepare, 2, 32)!;
            Assert.That(setter[0].GetImmediate(1), Is.Zero);
            // Keep both caller targets and the wrapper unchanged; only the clear
            // routine's immediate changes. A shared call address proves no effect.
            image[checked((int)pe.MapVirtualAddressToRaw(setter[0].NextIP - 4, false))] = 1;
            var metadataBytes = File.ReadAllBytes(metadata);
            var nullGuard = X64NativeInstructionReader.Read(pe, X64UnwindProof.ForApplication(app)!,
                proof.NullGuard, 6, 64)!;
            var nullGuardLength = checked((uint)(nullGuard[^1].NextIP - proof.NullGuard));
            foreach (var relativeStart in new[] { -7, 0, checked((int)nullGuardLength - 1) })
            {
                var relocated = WithRelocation(pe, proof.NullGuard, relativeStart);
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(relocated, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
                var changedApp = Cpp2IlApi.CurrentAppContext!;
                var changedPe = (PE)changedApp.Binary;
                var index = X64UnwindProof.ForApplication(changedApp)!;
                Assert.That(X64NativeInstructionReader.Read(changedPe, index,
                    proof.NullGuard, 6, 64), Is.EqualTo(nullGuard),
                    "File decoding preserves the guard, but the loader would rewrite overlapping instructions.");
                Assert.That(index.MatchesUnwind(proof.NullGuard, nullGuard[^1].NextIP,
                    4, 0, new byte[] { 4, 0x42 }), Is.True);
                Assert.That(X86RuntimeNullThrowProof.TryIdentify(changedApp,
                    nullGuard[5].NearBranchTarget), Is.Not.Null);
                Assert.That(X64PeOnceFlagProof.IsUnrelocatedRange(changedPe, index,
                    proof.NullGuard, nullGuardLength), Is.False);
                Assert.That(X64TerminalManagedThrowProof.ProveNullCheck(changedApp,
                    changedPe, index, proof.NullGuard), Is.False);
                var changed = changedApp.GetAssemblyByName("ExceptionRegionFixture")!.Types
                    .SelectMany(type => type.Methods).Single(candidate => candidate.Name == "CatchZero");
                _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(changedApp);
                Assert.That(X64ManagedThrowHelperProof.Check(changed,
                    proof.Allocator, proof.NullGuard, proof.Raiser), Is.False);
                Assert.That(X64CatchDivideRecovery.TryGenerate(changed,
                    changed.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!), Is.False);
            }
            var wrongNullExit = File.ReadAllBytes(binary);
            BinaryPrimitives.WriteInt32LittleEndian(wrongNullExit.AsSpan(
                checked((int)pe.MapVirtualAddressToRaw(nullGuard[5].IP + 1, false)), 4),
                checked((int)((long)proof.Raiser - (long)nullGuard[5].NextIP)));
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(wrongNullExit, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
            var nullChangedApp = Cpp2IlApi.CurrentAppContext!;
            var nullChanged = nullChangedApp.GetAssemblyByName("ExceptionRegionFixture")!.Types
                .SelectMany(type => type.Methods).Single(candidate => candidate.Name == "CatchZero");
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(nullChangedApp);
            Assert.That(X64ManagedThrowHelperProof.Check(nullChanged,
                proof.Allocator, proof.NullGuard, proof.Raiser), Is.False,
                "The null-exit call must reach the independently proved null-reference throw.");
            Assert.That(X64CatchDivideRecovery.TryGenerate(nullChanged,
                nullChanged.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!), Is.False);
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(image, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
            var alteredApp = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(alteredApp);
            var altered = alteredApp.GetAssemblyByName("ExceptionRegionFixture")!.Types
                .SelectMany(type => type.Methods).Single(candidate => candidate.Name == "CatchZero");
            Assert.That(X64ManagedThrowHelperProof.Check(altered, proof.Allocator, proof.NullGuard, proof.Raiser),
                Is.False, "A nonzero trace write must not be recovered as managed throw.");
            Assert.That(X64TerminalManagedThrowProof.ProveRaiseWrapper(alteredApp, (PE)alteredApp.Binary,
                X64UnwindProof.ForApplication(alteredApp)!, proof.Raiser), Is.False);
            Assert.That(X64CatchDivideRecovery.TryGenerate(altered,
                altered.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!), Is.False);

            var api = X64NativeInstructionReader.Read(pe, X64UnwindProof.ForApplication(app)!,
                pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_raise_exception"), 3, 48)!;
            foreach (var span in new[] { raiser, setter, api })
            {
                var start = span[0].IP;
                var length = checked((uint)(span[^1].NextIP - start));
                AssertTerminalAliasRejected(app, pe, method, proof.Raiser, start);
                foreach (var relativeStart in new[] { -7, 0, checked((int)length - 1) })
                {
                    var relocated = WithRelocation(pe, start, relativeStart);
                    Cpp2IlApi.ResetInternalState();
                    Cpp2IlApi.InitializeLibCpp2Il(relocated, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
                    var relocatedApp = Cpp2IlApi.CurrentAppContext!;
                    var relocatedPe = (PE)relocatedApp.Binary;
                    var index = X64UnwindProof.ForApplication(relocatedApp)!;
                    Assert.That(X64CodegenRaiseExceptionProof.HasTraceLayout(relocatedApp), Is.True);
                    Assert.That(X64CodegenRaiseExceptionProof.TryProve(proof.Raiser,
                        (address, count) => X64NativeInstructionReader.Read(relocatedPe,
                            index, address, count, count * 15),
                        relocatedPe.GetVirtualAddressOfExportedFunctionByName,
                        regions => X64CodegenRaiseExceptionProof.AllowsUnwind(index, regions)), Is.True,
                        "The relocation leaves the file bytes, export anchor and unwind proof unchanged.");
                    Assert.That(X64PeOnceFlagProof.IsUnrelocatedRange(relocatedPe, index, start, length), Is.False);
                    Assert.That(X64CodegenRaiseExceptionProof.TryIdentify(relocatedApp, proof.Raiser), Is.False,
                        $"A loader relocation at code offset {relativeStart} must invalidate this helper span.");
                    Assert.That(X64TerminalManagedThrowProof.ProveRaiseWrapper(relocatedApp,
                        relocatedPe, index, proof.Raiser), Is.False);
                    var relocatedMethod = relocatedApp.GetAssemblyByName("ExceptionRegionFixture")!.Types
                        .SelectMany(type => type.Methods).Single(candidate => candidate.Name == "CatchZero");
                    _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(relocatedApp);
                    Assert.That(X64CatchDivideRecovery.TryGenerate(relocatedMethod,
                        relocatedMethod.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!), Is.False);
                }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void AssertTerminalAliasRejected(ApplicationAnalysisContext app,
        PE pe, MethodAnalysisContext method, ulong raiser, ulong alias)
    {
        Assert.That(app.MethodsByAddress.ContainsKey(alias), Is.False);
        try
        {
            app.MethodsByAddress.Add(alias, [method]);
            Assert.That(X64CodegenRaiseExceptionProof.TryIdentify(app, raiser), Is.True,
                "The reusable native effect proof is separate from this route's ownership boundary.");
            Assert.That(X64TerminalManagedThrowProof.ProveRaiseWrapper(app, pe,
                X64UnwindProof.ForApplication(app)!, raiser), Is.False);
        }
        finally { app.MethodsByAddress.Remove(alias); }
    }

    [Test]
    [NonParallelizable]
    public void ExactTerminalThrowRejectsRelocatedMetadataUsageIdentity()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_THROW_ONLY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_THROW_ONLY_FIXTURE_INPUT to the synthetic exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("ThrowOnlyFixture")!.Types
                .SelectMany(type => type.Methods).Single(candidate => candidate.Name == "ThrowNotSupported");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(X64TerminalManagedThrowProof.Find(method, native), Is.Not.Null);
            var pe = (PE)app.Binary;
            var typeSlot = native[2].IPRelativeMemoryAddress;
            var methodSlot = native[12].IPRelativeMemoryAddress;
            var constructor = native[11].NearBranchTarget;
            var metadataBytes = File.ReadAllBytes(metadata);
            foreach (var slot in new[] { typeSlot, methodSlot })
            {
                foreach (var relativeStart in new[] { -7, 0, 7 })
                {
                    var relocated = WithRelocation(pe, slot, relativeStart);
                    Cpp2IlApi.ResetInternalState();
                    Cpp2IlApi.InitializeLibCpp2Il(relocated, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
                    var changedApp = Cpp2IlApi.CurrentAppContext!;
                    var changed = changedApp.GetAssemblyByName("ThrowOnlyFixture")!.Types
                        .SelectMany(type => type.Methods).Single(candidate => candidate.Name == "ThrowNotSupported");
                    changed.EnsureRawBytes();
                    var changedPe = (PE)changedApp.Binary;
                    var index = X64UnwindProof.ForApplication(changedApp)!;
                    Assert.That(changed.RawBytes.AsSpan().ToArray(), Is.EqualTo(method.RawBytes.AsSpan().ToArray()));
                    Assert.That(X64PeOnceFlagProof.IsUnrelocatedRange(changedPe, index, slot, 8), Is.False);
                    Assert.That(X64TerminalManagedThrowProof.BindMetadata(changed, changedPe,
                        index, typeSlot, methodSlot, constructor), Is.Null,
                        "Unchanged encoded metadata in the file cannot establish its loaded identity.");
                    Assert.That(X64TerminalManagedThrowProof.Find(changed,
                        X86Utils.Iterate(changed).ToArray()), Is.Null);
                }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static byte[] WithRelocation(PE pe, ulong address, int relativeStart)
    {
        var image = pe.GetRawBinaryContent().ToArray();
        var header = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x3C, 4)));
        var optional = header + 24;
        var imageBase = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(optional + 24, 8));
        var relocationRva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(optional + 112 + 5 * 8, 4));
        var raw = checked((int)pe.MapVirtualAddressToRaw(imageBase + relocationRva, false));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(raw + 4, 4)), Is.GreaterThanOrEqualTo(10));
        var targetRva = checked((uint)((long)address + relativeStart - (long)imageBase));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(raw, 4), targetRva & ~0xFFFU);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(raw + 8, 2),
            (ushort)(0xA000U | (targetRva & 0xFFFU)));
        return image;
    }

    private sealed class NativeFixture
    {
        internal readonly Dictionary<ulong, byte[]> Bodies = [];
        internal ulong ExportAddress = 0x3000;
        internal Func<IReadOnlyList<X64CodegenRaiseExceptionProof.Region>, bool> Unwind = _ => true;

        internal NativeFixture()
        {
            Bodies[0x1000] = new Builder(0x1000).Hex("48895C2408574883EC20488BF9488BDA4883C138")
                .Call(0x2000).Hex("488D4F40").Call(0x2000).Hex("488BD3488BCF").Call(0x4000).Bytes.ToArray();
            Bodies[0x2000] = Convert.FromHexString("48C70100000000C3");
            Bodies[0x3000] = new Builder(0x3000).Hex("4883EC2833D2").Call(0x4000).Bytes.ToArray();
        }

        internal bool Proves() => X64CodegenRaiseExceptionProof.TryProve(0x1000, Read,
            name => name == "il2cpp_raise_exception" ? ExportAddress : 0, Unwind);

        internal IReadOnlyList<Instruction>? Read(ulong address, int count)
        {
            if (!Bodies.TryGetValue(address, out var bytes))
                return null;
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), address);
            var body = new Instruction[count];
            for (var i = 0; i < count; i++)
                body[i] = decoder.Decode();
            return body;
        }

        internal void Replace(ulong address, int index, string replacement)
        {
            var instruction = Read(address, index + 1)![index];
            var offset = checked((int)(instruction.IP - address));
            var bytes = Bodies[address];
            Bodies[address] = [.. bytes[..offset], .. Convert.FromHexString(replacement),
                .. bytes[(offset + instruction.Length)..]];
        }
    }

    private sealed class Builder(ulong start)
    {
        internal readonly List<byte> Bytes = [];
        internal Builder Hex(string value) { Bytes.AddRange(Convert.FromHexString(value)); return this; }
        internal Builder Call(ulong target)
        {
            Bytes.Add(0xE8);
            Bytes.AddRange(BitConverter.GetBytes(checked((int)((long)target - (long)start - Bytes.Count - 4))));
            return this;
        }
    }
}
