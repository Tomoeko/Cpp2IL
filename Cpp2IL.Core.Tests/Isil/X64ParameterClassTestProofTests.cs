using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ParameterClassTestProofTests
{
    [Test]
    public void ParameterClassTestPreservesItsDistinctNativeSource()
    {
        var native = CreateNativeBody();
        var shape = X64ParameterClassTestProof.TryProveShape(native);
        Assert.Multiple(() =>
        {
            Assert.That(shape?.OnceFlag, Is.EqualTo(0x4000));
            Assert.That(shape?.TypeInfoSlot, Is.EqualTo(0x3000));
            Assert.That(shape?.MetadataInitializer, Is.EqualTo(0x5000));
            Assert.That(X64ClassCastLookupProof.TryProveShape(native), Is.Null,
                "the parameter source must not acquire a field-read contract");
        });
    }

    [TestCase("preserved-argument")]
    [TestCase("null-input")]
    [TestCase("null-branch")]
    [TestCase("null-result")]
    [TestCase("type-slot")]
    [TestCase("object-class")]
    [TestCase("depth-offset")]
    [TestCase("depth-width")]
    [TestCase("depth-register")]
    [TestCase("signed-depth")]
    [TestCase("depth-exit")]
    [TestCase("hierarchy-offset")]
    [TestCase("hierarchy-index")]
    [TestCase("hierarchy-scale")]
    [TestCase("hierarchy-target")]
    [TestCase("hierarchy-exit")]
    [TestCase("success-bit")]
    [TestCase("success-reference")]
    [TestCase("failure-bit")]
    [TestCase("failure-reference")]
    [TestCase("stack-exit")]
    [TestCase("once-flag")]
    [TestCase("once-store")]
    [TestCase("metadata-target")]
    [TestCase("discontinuous")]
    [TestCase("non64bit")]
    [TestCase("lock-prefix")]
    [TestCase("segment-prefix")]
    [TestCase("trailing-operation")]
    public void ChangedSourceGuardsHierarchyAndExitsRemainUnproved(string mutation)
    {
        var native = CreateNativeBody();
        switch (mutation)
        {
            case "preserved-argument": native[3].Op1Register = Register.RDX; break;
            case "null-input": native[8].Op0Register = Register.RCX; break;
            case "null-branch": native[9].NearBranch64 = native[15].IP; break;
            case "null-result": native[10].Op1Register = Register.ECX; break;
            case "type-slot": native[14].MemoryDisplacement64++; break;
            case "object-class": native[15].MemoryBase = Register.RCX; break;
            case "depth-offset": native[16].MemoryDisplacement64++; break;
            case "depth-width": native[16].Code = Code.Movzx_r32_rm16; break;
            case "depth-register": native[17].Op1Register = Register.DL; break;
            case "signed-depth": native[18].Code = Code.Jl_rel8_64; break;
            case "depth-exit": native[18].NearBranch64 = native[22].IP; break;
            case "hierarchy-offset": native[19].MemoryDisplacement64 += 8; break;
            case "hierarchy-index": native[20].MemoryIndex = Register.RDX; break;
            case "hierarchy-scale": native[20].MemoryIndexScale = 4; break;
            case "hierarchy-target": native[20].Op1Register = Register.R8; break;
            case "hierarchy-exit": native[21].NearBranch64 = native[22].IP; break;
            case "success-bit": native[23].Immediate8 = 0; break;
            case "success-reference": native[25].Op1Register = Register.RDX; break;
            case "failure-bit": native[30].Op1Register = Register.DL; break;
            case "failure-reference": native[32].Op1Register = Register.RDX; break;
            case "stack-exit": native[33].Immediate8 = 0x28; break;
            case "once-flag": native[7].MemoryDisplacement64++; break;
            case "once-store": native[7].Immediate8 = 2; break;
            case "metadata-target": native[6].NearBranch64 = 0; break;
            case "discontinuous": native[16].IP++; break;
            case "non64bit": native[0].CodeSize = CodeSize.Code32; break;
            case "lock-prefix": native[0].HasLockPrefix = true; break;
            case "segment-prefix": native[15].SegmentPrefix = Register.FS; break;
            case "trailing-operation": native = [.. native, native[^1]]; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.That(X64ParameterClassTestProof.TryProveShape(native), Is.Null);
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerWithTargetInitializerRemainsUnresolved()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_PARAMETER_CLASS_TEST_INITIALIZER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_PARAMETER_CLASS_TEST_INITIALIZER_FIXTURE_INPUT to the synthetic initializer player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var method = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ParameterClassTestFixture")!.Types
                .SelectMany(type => type.Methods)
                .Single(candidate => candidate.Name == "AsException");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            var shape = X64ParameterClassTestProof.TryProveShape(native);
            Assert.Multiple(() =>
            {
                Assert.That(shape, Is.Not.Null);
                Assert.That(method.ReturnType.Definition!.HasCctor, Is.True);
                Assert.That(X64ParameterClassTestProof.BindProvedShape(method, shape!), Is.Null,
                    "a matched class-test caller does not prove target initialization effects");
                Assert.That(X64ParameterClassTestProof.Find(method, native), Is.Null);
            });
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerRequiresUnchangedSignatureMetadataAndCompleteNativeBody()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_PARAMETER_CLASS_TEST_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_PARAMETER_CLASS_TEST_FIXTURE_INPUT to the synthetic player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("ParameterClassTestFixture")!.Types
                .SelectMany(type => type.Methods)
                .Single(candidate => candidate.Name == "AsRemote");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            var shape = X64ParameterClassTestProof.TryProveShape(native);
            Assert.Multiple(() =>
            {
                Assert.That(native.Length, Is.EqualTo(36));
                Assert.That(shape, Is.Not.Null);
                Assert.That(X64ParameterClassTestProof.Find(method, native),
                    Is.SameAs(method.ReturnType));
            });

            Assert.Multiple(() =>
            {
                Assert.That(X64ParameterClassTestProof.BindProvedShape(method,
                    shape! with { TypeInfoSlot = shape.TypeInfoSlot + 8 }), Is.Null);
                Assert.That(X64ParameterClassTestProof.BindProvedShape(method,
                    shape! with { OnceFlag = shape.TypeInfoSlot }), Is.Null);
                Assert.That(X64ParameterClassTestProof.BindProvedShape(method,
                    shape! with { MetadataInitializer = method.UnderlyingPointer }), Is.Null);
            });
            try
            {
                method.Parameters[0].OverrideParameterType = app.SystemTypes.SystemStringType;
                Assert.That(X64ParameterClassTestProof.Find(method, native), Is.Null);
            }
            finally { method.Parameters[0].OverrideParameterType = null; }
            try
            {
                method.OverrideReturnType = app.SystemTypes.SystemObjectType;
                Assert.That(X64ParameterClassTestProof.Find(method, native), Is.Null);
            }
            finally { method.OverrideReturnType = null; }

            var target = method.ReturnType;
            try
            {
                target.OverrideBaseType = target;
                Assert.That(X64ParameterClassTestProof.Find(method, native), Is.Null,
                    "cyclic or edited hierarchies cannot establish this native class test");
            }
            finally { target.OverrideBaseType = null; }
            var bindings = app.MethodsByAddress[method.UnderlyingPointer];
            bindings.Add(method);
            try { Assert.That(X64ParameterClassTestProof.Find(method, native), Is.Null); }
            finally { bindings.RemoveAt(bindings.Count - 1); }
            var interior = method.UnderlyingPointer + 1;
            try
            {
                app.MethodsByAddress.Add(interior, [method]);
                Assert.That(X64ParameterClassTestProof.Find(method, native), Is.Null);
            }
            finally { app.MethodsByAddress.Remove(interior); }

            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            Assert.That(X64ParameterClassTestRecovery.TryGenerate(method, definition), Is.True);
            var originalBytes = method.RawBytes;
            try
            {
                var changedBytes = originalBytes.AsSpan().ToArray();
                changedBytes[0] ^= 1;
                method.RawBytes = new BinarySlice(changedBytes);
                Assert.That(X64ParameterClassTestProof.Find(method, native), Is.Null,
                    "A nonempty changed cache must be checked against current player bytes.");
                Assert.That(X64ParameterClassTestRecovery.TryGenerate(method, definition), Is.False,
                    "Final emission cannot refresh away a changed nonempty native prefix.");
            }
            finally { method.RawBytes = originalBytes; }
            Assert.That(X64ParameterClassTestRecovery.TryGenerate(method, definition), Is.True);

            var changed = File.ReadAllBytes(binary);
            var offset = checked((int)((PE)app.Binary).MapVirtualAddressToRaw(
                method.UnderlyingPointer, false));
            changed[offset] ^= 1;
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changed, File.ReadAllBytes(metadata),
                UnityVersion.Parse("2021.3.35f1"));
            var changedMethod = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ParameterClassTestFixture")!.Types
                .SelectMany(type => type.Methods)
                .Single(candidate => candidate.Name == "AsRemote");
            Assert.That(X64ParameterClassTestProof.Find(changedMethod, native), Is.Null,
                "a stale instruction array cannot authenticate altered executable bytes");

            foreach (var relativeStart in new[] { -7, 0, 7 })
            {
                var relocated = WithSlotRelocation((PE)app.Binary,
                    shape!.TypeInfoSlot, relativeStart);
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(relocated, File.ReadAllBytes(metadata),
                    UnityVersion.Parse("2021.3.35f1"));
                var relocatedApp = Cpp2IlApi.CurrentAppContext!;
                var relocatedMethod = relocatedApp
                    .GetAssemblyByName("ParameterClassTestFixture")!.Types
                    .SelectMany(type => type.Methods)
                    .Single(candidate => candidate.Name == "AsRemote");
                Assert.Multiple(() =>
                {
                    Assert.That(X64PeOnceFlagProof.IsUnrelocatedRange(
                        (PE)relocatedApp.Binary, X64UnwindProof.ForApplication(relocatedApp)!,
                        shape.TypeInfoSlot, 8), Is.False);
                    Assert.That(X64ParameterClassTestProof.Find(relocatedMethod,
                        X86Utils.Iterate(relocatedMethod).ToArray()), Is.Null,
                        $"a loader relocation at slot offset {relativeStart} can change target identity");
                });
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static byte[] WithSlotRelocation(PE pe, ulong slot, int relativeStart)
    {
        var image = pe.GetRawBinaryContent().ToArray();
        var header = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(0x3C, 4)));
        var optional = header + 24;
        var imageBase = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(optional + 24, 8));
        var relocationRva = BinaryPrimitives.ReadUInt32LittleEndian(
            image.AsSpan(optional + 112 + 5 * 8, 4));
        var raw = checked((int)pe.MapVirtualAddressToRaw(imageBase + relocationRva, false));
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(raw + 4, 4)),
            Is.GreaterThanOrEqualTo(10));
        var targetRva = checked((uint)((long)slot + relativeStart - (long)imageBase));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(raw, 4), targetRva & ~0xFFFU);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(raw + 8, 2),
            (ushort)(0xA000U | (targetRva & 0xFFFU)));
        return image;
    }

    // Encode individual synthetic instructions, then bind neutral RIP and branch
    // addresses. No player-specific bytes, addresses or binary digest is retained.
    private static Instruction[] CreateNativeBody()
    {
        string[] fragments =
        [
            "4053", "4883EC20", "803D0000000000", "488BD9", "7500",
            "488D0D00000000", "E800000000", "C6050000000001",
            "4885DB", "7500", "33C0", "4883C420", "5B", "C3",
            "488B1500000000", "488B03", "0FB68A2C010000", "38882C010000",
            "7200", "488B80C8000000", "483954C8F8", "7500",
            "33C0", "B101", "84C9", "480F45C3", "4883C420", "5B", "C3",
            "33C0", "32C9", "84C9", "480F45C3", "4883C420", "5B", "C3"
        ];
        var result = new Instruction[fragments.Length];
        var address = 0x1000UL;
        for (var index = 0; index < fragments.Length; index++)
        {
            result[index] = Decoder.Create(64,
                new ByteArrayCodeReader(Convert.FromHexString(fragments[index])),
                address).Decode();
            address = result[index].NextIP;
        }
        result[2].MemoryDisplacement64 = result[7].MemoryDisplacement64 = 0x4000;
        result[5].MemoryDisplacement64 = result[14].MemoryDisplacement64 = 0x3000;
        result[4].NearBranch64 = result[8].IP;
        result[6].NearBranch64 = 0x5000;
        result[9].NearBranch64 = result[14].IP;
        result[18].NearBranch64 = result[21].NearBranch64 = result[29].IP;
        return result;
    }
}
