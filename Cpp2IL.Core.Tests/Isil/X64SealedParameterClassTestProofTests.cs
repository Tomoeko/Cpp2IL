using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;
using ManagedMethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;

namespace Cpp2IL.Core.Tests.Isil;

public class X64SealedParameterClassTestProofTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void CompleteClassEqualityPreservesOriginalReferenceOrBoolean(bool booleanResult)
    {
        var body = CreateNativeBody(booleanResult);
        var shape = X64SealedParameterClassTestProof.TryProveShape(body, booleanResult);
        Assert.Multiple(() =>
        {
            Assert.That(shape, Is.Not.Null);
            Assert.That(shape!.OnceFlag, Is.EqualTo(0x4000));
            Assert.That(shape.TypeInfoSlot, Is.EqualTo(0x3000));
            Assert.That(shape.MetadataInitializer, Is.EqualTo(0x5000));
            Assert.That(X64SealedParameterClassTestProof.TryProveShape(body, !booleanResult), Is.Null);
            Assert.That(X64ParameterClassTestProof.TryProveShape(body), Is.Null);
            Assert.That(X64BooleanParameterClassTestProof.TryProveShape(body), Is.Null);
        });
    }

    private static IEnumerable<TestCaseData> ChangedShapes()
    {
        string[] mutations = ["saved-register", "prolog-size", "stack-frame", "metadata-order",
            "source", "once-store", "metadata-call", "zero-result", "null-source", "null-branch",
            "type-slot", "type-register", "class-base", "class-offset", "class-index", "class-width",
            "class-target", "select-condition", "select-source", "select-result", "return-frame",
            "discontinuous", "prefix", "non64bit", "added-operation"];
        foreach (var booleanResult in new[] { false, true })
            foreach (var mutation in mutations)
                yield return new TestCaseData(booleanResult, mutation);
        yield return new TestCaseData(true, "boolean-test");
        yield return new TestCaseData(true, "boolean-condition");
        yield return new TestCaseData(true, "boolean-register");
    }

    [TestCaseSource(nameof(ChangedShapes))]
    public void ChangedInitializationNullClassIdentityAndOutputsStayUnproved(bool booleanResult, string mutation)
    {
        var body = CreateNativeBody(booleanResult);
        switch (mutation)
        {
            case "saved-register": body[0].Op0Register = Register.RDI; break;
            case "prolog-size": body[0].Length++; break;
            case "stack-frame": body[1].Immediate8 = 0x28; break;
            case "metadata-order": body[4].NearBranch64 = body[9].IP; break;
            case "source": body[3].Op1Register = Register.RDX; break;
            case "once-store": body[7].MemoryDisplacement64++; break;
            case "metadata-call": body[6].NearBranch64 = 0; break;
            case "zero-result": body[8].Op1Register = Register.EDX; break;
            case "null-source": body[9].Op1Register = Register.RDX; break;
            case "null-branch": body[10].NearBranch64 = body[11].IP; break;
            case "type-slot": body[11].MemoryDisplacement64++; break;
            case "type-register": body[11].Op0Register = Register.RDX; break;
            case "class-base": body[12].MemoryBase = Register.RCX; break;
            case "class-offset": body[12].MemoryDisplacement64 = 8; break;
            case "class-index": body[12].MemoryIndex = Register.RDX; break;
            case "class-width": body[12].Code = Code.Cmp_rm32_r32; break;
            case "class-target": body[12].Op1Register = Register.RDX; break;
            case "select-condition": body[13].Code = Code.Cmovne_r64_rm64; break;
            case "select-source": body[13].Op1Register = Register.RDX; break;
            case "select-result": body[13].Op0Register = Register.RDX; break;
            case "return-frame": body[booleanResult ? 16 : 14].Immediate8 = 0x28; break;
            case "discontinuous": body[11].IP++; break;
            case "prefix": body[12].HasRepPrefix = true; break;
            case "non64bit": body[0].CodeSize = CodeSize.Code32; break;
            case "added-operation": body = [.. body, body[^1]]; break;
            case "boolean-test": body[14].Op0Register = Register.RAX; break;
            case "boolean-condition": body[15].Code = Code.Sete_rm8; break;
            case "boolean-register": body[15].Op0Register = Register.CL; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.That(X64SealedParameterClassTestProof.TryProveShape(body, booleanResult), Is.Null);
    }

    [TestCase("AsBuilder", false)]
    [TestCase("IsBuilder", true)]
    [NonParallelizable]
    public void ExactPlayerBindsSealedTargetAndRechecksMetadataBeforeEmission(string name, bool booleanResult)
    {
        var (binary, metadata) = InputPaths();
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = SelectedMethod(app, name);
            var body = X64NativeInstructionReader.ReadRootBody(method)!;
            var shape = X64SealedParameterClassTestProof.TryProveShape(body, booleanResult)!;
            var target = X64SealedParameterClassTestProof.Find(method, body);
            Assert.Multiple(() =>
            {
                Assert.That(body.Length, Is.EqualTo(booleanResult ? 19 : 17));
                Assert.That(target, Is.Not.Null);
                Assert.That(target!.FullName, Is.EqualTo("System.Text.StringBuilder"));
                Assert.That(target.IsSealed, Is.True);
                Assert.That(target.Definition!.HasCctor, Is.False);
                Assert.That(X64ParameterClassTestProof.Find(method, body), Is.Null);
                Assert.That(X64BooleanParameterClassTestProof.Find(method, body), Is.Null);
            });
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            Assert.That(X64SealedParameterClassTestRecovery.TryGenerate(method, definition), Is.True);
            var expected = booleanResult
                ? new[] { CilOpCodes.Ldarg_0, CilOpCodes.Isinst, CilOpCodes.Ldnull, CilOpCodes.Cgt_Un, CilOpCodes.Ret }
                : new[] { CilOpCodes.Ldarg_0, CilOpCodes.Isinst, CilOpCodes.Ret };
            Assert.That(definition.CilMethodBody!.Instructions.Select(instruction => instruction.OpCode), Is.EqualTo(expected));

            void Reject(Action mutate, Action restore)
            {
                try
                {
                    mutate();
                    Assert.That(X64SealedParameterClassTestProof.Find(method, body), Is.Null);
                    Assert.That(X64SealedParameterClassTestRecovery.TryGenerate(method, definition), Is.False);
                }
                finally { restore(); }
                Assert.That(X64SealedParameterClassTestRecovery.TryGenerate(method, definition), Is.True);
            }
            var targetDefinition = target!.Definition!;
            var flags = targetDefinition.Flags;
            Reject(() => targetDefinition.Flags = flags & ~(uint)TypeAttributes.Sealed,
                () => targetDefinition.Flags = flags);
            var bits = targetDefinition.Bitfield;
            Reject(() => targetDefinition.Bitfield = bits | (1u << 3), () => targetDefinition.Bitfield = bits);
            Reject(() => target.OverrideName = "DifferentClass", () => target.OverrideName = null);
            Reject(() => target.OverrideNamespace = "DifferentNamespace", () => target.OverrideNamespace = null);
            Reject(() => target.OverrideBaseType = target, () => target.OverrideBaseType = null);
            Reject(() => method.Parameters[0].OverrideParameterType = app.SystemTypes.SystemStringType,
                () => method.Parameters[0].OverrideParameterType = null);
            Reject(() => method.OverrideReturnType = app.SystemTypes.SystemObjectType,
                () => method.OverrideReturnType = null);
            var originalBytes = method.RawBytes;
            var changedBytes = originalBytes.AsSpan().ToArray();
            changedBytes[0] ^= 1;
            Reject(() => method.RawBytes = new BinarySlice(changedBytes),
                () => method.RawBytes = originalBytes);
            var aliases = app.MethodsByAddress[method.UnderlyingPointer];
            Reject(() => aliases.Add(method), () => aliases.RemoveAt(aliases.Count - 1));
            Reject(() => app.MethodsByAddress.Add(method.UnderlyingPointer + 1, [method]),
                () => app.MethodsByAddress.Remove(method.UnderlyingPointer + 1));
            Assert.That(X64SealedParameterClassTestProof.BindProvedShape(method,
                shape with { TypeInfoSlot = shape.OnceFlag }, booleanResult), Is.Null);
            Assert.That(X64SealedParameterClassTestProof.BindProvedShape(method,
                shape with { MetadataInitializer = method.UnderlyingPointer }, booleanResult), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerRejectsChangedSelectAndCanonicalBooleanBytes()
    {
        var (binary, metadata) = InputPaths();
        var image = File.ReadAllBytes(binary);
        var metadataBytes = File.ReadAllBytes(metadata);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            foreach (var (name, index, replacement) in new[]
                     { ("AsBuilder", 13, (byte)0x45), ("IsBuilder", 15, (byte)0x94) })
            {
                Cpp2IlApi.InitializeLibCpp2Il(image, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
                var app = Cpp2IlApi.CurrentAppContext!;
                var method = SelectedMethod(app, name);
                var body = X64NativeInstructionReader.ReadRootBody(method)!;
                Assert.That(X64SealedParameterClassTestProof.Find(method, body), Is.Not.Null);
                var pe = (PE)app.Binary;
                var changed = image.ToArray();
                // CMOVE has REX,0F,44; SETNE has 0F,95. Alter only the condition.
                var opcode = body[index].IP + (name == "AsBuilder" ? 2UL : 1UL);
                changed[checked((int)pe.MapVirtualAddressToRaw(opcode, false))] = replacement;
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
                var altered = SelectedMethod(Cpp2IlApi.CurrentAppContext!, name);
                Assert.That(X64SealedParameterClassTestProof.Find(altered, body), Is.Null,
                    "A stale decode cannot authenticate changed class selection or Boolean output.");
                Assert.That(X64SealedParameterClassTestRecovery.TryGenerate(altered, RejectedDefinition()), Is.False);
                Cpp2IlApi.ResetInternalState();
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerRejectsLoaderOverlapAcrossCallerMetadataAndAuthoritativeUnwind()
    {
        var (binary, metadata) = InputPaths();
        var image = File.ReadAllBytes(binary);
        var metadataBytes = File.ReadAllBytes(metadata);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            foreach (var name in new[] { "AsBuilder", "IsBuilder" })
            {
                Cpp2IlApi.InitializeLibCpp2Il(image, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
                var app = Cpp2IlApi.CurrentAppContext!;
                var method = SelectedMethod(app, name);
                var pe = (PE)app.Binary;
                var body = X64NativeInstructionReader.ReadRootBody(method)!;
                var shape = X64SealedParameterClassTestProof.TryProveShape(body, name == "IsBuilder")!;
                Assert.That(X64SealedParameterClassTestProof.Find(method, body), Is.Not.Null);
                var targets = new List<(string Name, ulong Address, int Length)>
                {
                    ("caller", method.UnderlyingPointer, checked((int)(body[^1].NextIP - method.UnderlyingPointer))),
                    ("once-flag", shape.OnceFlag, 1), ("typeinfo-slot", shape.TypeInfoSlot, 8),
                    ("initializer", shape.MetadataInitializer, 5)
                };
                targets.AddRange(UnwindTargets(pe, method.UnderlyingPointer));
                foreach (var (targetName, address, length) in targets)
                    foreach (var displacement in new[] { -7, 0, length - 1 }.Distinct())
                    {
                        var changed = WithRelocation(pe, address, displacement);
                        Cpp2IlApi.ResetInternalState();
                        Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
                        var altered = SelectedMethod(Cpp2IlApi.CurrentAppContext!, name);
                        var raw = checked((int)Cpp2IlApi.CurrentAppContext!.Binary.MapVirtualAddressToRaw(method.UnderlyingPointer));
                        var unchangedDecode = X86Utils.Disassemble(changed.AsSpan(raw,
                            checked((int)(body[^1].NextIP - method.UnderlyingPointer))), method.UnderlyingPointer, false).ToArray();
                        Assert.That(unchangedDecode, Is.EqualTo(body), "Only relocation records change, not caller file bytes.");
                        Assert.That(X64SealedParameterClassTestProof.TryProveShape(unchangedDecode, name == "IsBuilder"), Is.Not.Null);
                        if (targetName is "caller" or "function-table-record" or "unwind-header" or "unwind-codes")
                            Assert.That(X64NativeInstructionReader.ReadRootBody(altered), Is.Null);
                        Assert.That(X64SealedParameterClassTestProof.Find(altered, body), Is.Null, targetName);
                        Assert.That(X64SealedParameterClassTestRecovery.TryGenerate(altered, RejectedDefinition()), Is.False, targetName);
                    }
                Cpp2IlApi.ResetInternalState();
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static MethodDefinition RejectedDefinition()
    {
        var module = new ModuleDefinition("SealedClassTestProof.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        return new MethodDefinition("Rejected", ManagedMethodAttributes.Public | ManagedMethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Object, [module.CorLibTypeFactory.Object]));
    }

    private static (string Binary, string Metadata) InputPaths()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_SEALED_PARAMETER_CLASS_TEST_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_SEALED_PARAMETER_CLASS_TEST_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        return (binary, metadata);
    }

    private static MethodAnalysisContext SelectedMethod(ApplicationAnalysisContext app, string name)
    {
        var method = app.GetAssemblyByName("SealedParameterClassTestFixture")!.Types.SelectMany(type => type.Methods)
            .Single(method => method.Name == name);
        method.EnsureRawBytes();
        return method;
    }

    private static List<(string Name, ulong Address, int Length)> UnwindTargets(PE pe, ulong methodAddress)
    {
        var image = pe.GetRawBinaryContent();
        var header = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(0x3C, 4)));
        var optional = header + 24;
        var imageBase = BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(optional + 24, 8));
        var directory = optional + 112 + 3 * 8;
        var rva = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(directory, 4));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(directory + 4, 4));
        var raw = checked((int)pe.MapVirtualAddressToRaw(imageBase + rva, false));
        for (var offset = 0; offset < size; offset += 12)
        {
            var entry = image.Slice(raw + offset, 12);
            if (imageBase + BinaryPrimitives.ReadUInt32LittleEndian(entry[..4]) != methodAddress)
                continue;
            var address = imageBase + BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8, 4));
            var unwindRaw = checked((int)pe.MapVirtualAddressToRaw(address, false));
            var count = image[unwindRaw + 2];
            return [("function-table-record", imageBase + rva + (uint)offset, 12),
                ("unwind-header", address, 4), ("unwind-codes", address + 4, 2 * count)];
        }
        throw new AssertionException("The selected exact method must own an unwind record.");
    }

    private static byte[] WithRelocation(PE pe, ulong address, int displacement)
    {
        var image = pe.GetRawBinaryContent().ToArray();
        var header = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x3C, 4)));
        var optional = header + 24;
        var imageBase = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(optional + 24, 8));
        var directory = optional + 112 + 5 * 8;
        var rva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(directory, 4));
        var raw = checked((int)pe.MapVirtualAddressToRaw(imageBase + rva, false));
        var targetRva = checked((uint)((long)address + displacement - (long)imageBase));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(directory + 4, 4), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(raw, 4), targetRva & ~0xFFFU);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(raw + 4, 4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(raw + 8, 2), (ushort)(0xA000U | (targetRva & 0xFFFU)));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(raw + 10, 2), 0);
        return image;
    }

    // Public synthetic instruction fragments have neutral addresses. Bind RIP
    // slots and branch edges after decoding; retain no linked player bytes.
    private static Instruction[] CreateNativeBody(bool booleanResult)
    {
        string[] prefix = ["4053", "4883EC20", "803D0000000000", "488BD9", "7500",
            "488D0D00000000", "E800000000", "C6050000000001"];
        string[] result = booleanResult
            ? ["33C9", "4885DB", "7400", "488B0500000000", "483903", "480F44CB", "4885C9", "0F95C0"]
            : ["33C0", "4885DB", "7400", "488B0D00000000", "48390B", "480F44C3"];
        var fragments = prefix.Concat(result).Concat(new[] { "4883C420", "5B", "C3" }).ToArray();
        var body = new Instruction[fragments.Length];
        var address = 0x1000UL;
        for (var index = 0; index < fragments.Length; index++)
        {
            body[index] = Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(fragments[index])), address).Decode();
            address = body[index].NextIP;
        }
        body[2].MemoryDisplacement64 = body[7].MemoryDisplacement64 = 0x4000;
        body[5].MemoryDisplacement64 = body[11].MemoryDisplacement64 = 0x3000;
        body[6].NearBranch64 = 0x5000;
        body[4].NearBranch64 = body[8].IP;
        body[10].NearBranch64 = body[14].IP;
        return body;
    }
}
