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

public class X64BooleanParameterClassTestProofTests
{
    [Test]
    public void BooleanOutputRetainsTheOriginalReferenceTestOnBothExits()
    {
        var body = CreateNativeBody();
        var shape = X64BooleanParameterClassTestProof.TryProveShape(body);
        Assert.Multiple(() =>
        {
            Assert.That(shape, Is.Not.Null);
            Assert.That(shape!.OnceFlag, Is.EqualTo(0x4000));
            Assert.That(shape.TypeInfoSlot, Is.EqualTo(0x3000));
            Assert.That(shape.MetadataInitializer, Is.EqualTo(0x5000));
            Assert.That(X64ParameterClassTestProof.TryProveShape(body), Is.Null,
                "A Boolean return cannot acquire the reference-return contract.");
        });
    }

    [TestCase("saved-register")]
    [TestCase("prolog-size")]
    [TestCase("stack-frame")]
    [TestCase("metadata-order")]
    [TestCase("source")]
    [TestCase("once-store")]
    [TestCase("metadata-call")]
    [TestCase("null-source")]
    [TestCase("null-branch")]
    [TestCase("null-reference")]
    [TestCase("null-test")]
    [TestCase("null-condition")]
    [TestCase("null-result-register")]
    [TestCase("type-slot")]
    [TestCase("source-class")]
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
    [TestCase("true-bit")]
    [TestCase("true-join")]
    [TestCase("false-bit")]
    [TestCase("result-reference")]
    [TestCase("result-test")]
    [TestCase("result-condition")]
    [TestCase("result-register")]
    [TestCase("return-frame")]
    [TestCase("discontinuous")]
    [TestCase("prefix")]
    [TestCase("non64bit")]
    [TestCase("added-operation")]
    public void ChangedInputHierarchyInitializationAndBooleanExitsRemainUnproved(string mutation)
    {
        var body = CreateNativeBody();
        switch (mutation)
        {
            case "saved-register": body[0].Op0Register = Register.RDI; break;
            case "prolog-size": body[0].Length++; body[1].IP++; break;
            case "stack-frame": body[1].Immediate8 = 0x28; break;
            case "metadata-order": body[4].NearBranch64 = body[16].IP; break;
            case "source": body[3].Op1Register = Register.RDX; break;
            case "once-store": body[7].MemoryDisplacement64++; break;
            case "metadata-call": body[6].NearBranch64 = 0; break;
            case "null-source": body[8].Op1Register = Register.RDX; break;
            case "null-branch": body[9].NearBranch64 = body[17].IP; break;
            case "null-reference": body[10].Op1Register = Register.ECX; break;
            case "null-test": body[11].Op0Register = Register.RBX; break;
            case "null-condition": body[12].Code = Code.Sete_rm8; break;
            case "null-result-register": body[12].Op0Register = Register.CL; break;
            case "type-slot": body[16].MemoryDisplacement64++; break;
            case "source-class": body[17].MemoryBase = Register.RCX; break;
            case "depth-offset": body[18].MemoryDisplacement64++; break;
            case "depth-width": body[18].Code = Code.Movzx_r32_rm16; break;
            case "depth-register": body[19].Op1Register = Register.DL; break;
            case "signed-depth": body[20].Code = Code.Jl_rel8_64; break;
            case "depth-exit": body[20].NearBranch64 = body[24].IP; break;
            case "hierarchy-offset": body[21].MemoryDisplacement64 += 8; break;
            case "hierarchy-index": body[22].MemoryIndex = Register.RDX; break;
            case "hierarchy-scale": body[22].MemoryIndexScale = 4; break;
            case "hierarchy-target": body[22].Op1Register = Register.R8; break;
            case "hierarchy-exit": body[23].NearBranch64 = body[24].IP; break;
            case "true-bit": body[24].Immediate8 = 0; break;
            case "true-join": body[25].NearBranch64 = body[26].IP; break;
            case "false-bit": body[26].Op1Register = Register.DL; break;
            case "result-reference": body[29].Op1Register = Register.RDX; break;
            case "result-test": body[30].Op0Register = Register.RBX; break;
            case "result-condition": body[31].Code = Code.Sete_rm8; break;
            case "result-register": body[31].Op0Register = Register.CL; break;
            case "return-frame": body[32].Immediate8 = 0x28; break;
            case "discontinuous": body[18].IP++; break;
            case "prefix": body[18].HasLockPrefix = true; break;
            case "non64bit": body[0].CodeSize = CodeSize.Code32; break;
            case "added-operation": body = [.. body, body[^1]]; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.That(X64BooleanParameterClassTestProof.TryProveShape(body), Is.Null);
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerBindsItsTypeInfoAndRechecksIdentityBeforeEmission()
    {
        var (binary, metadata) = InputPaths();
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = SelectedMethod(app);
            var region = X64UnwindProof.ForApplication(app)!.ClassifySpan(
                method.UnderlyingPointer, method.UnderlyingPointer + 1);
            var body = X86Utils.Iterate(method).TakeWhile(instruction => instruction.IP < region.End).ToArray();
            var shape = X64BooleanParameterClassTestProof.TryProveShape(body);
            Assert.Multiple(() =>
            {
                Assert.That(body, Has.Length.EqualTo(35));
                Assert.That(region.End - region.Start, Is.EqualTo(121));
                Assert.That(shape, Is.Not.Null);
                Assert.That(method.ReturnType, Is.SameAs(app.SystemTypes.SystemBooleanType));
            });
            var target = X64BooleanParameterClassTestProof.Find(method, body);
            Assert.That(target, Is.Not.Null);
            Assert.That(target!.FullName, Is.EqualTo("System.MarshalByRefObject"));
            Assert.That(target.Definition!.HasCctor, Is.False);
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            Assert.That(X64BooleanParameterClassTestRecovery.TryGenerate(method, definition), Is.True);
            Assert.That(definition.CilMethodBody!.Instructions.Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { CilOpCodes.Ldarg_0, CilOpCodes.Isinst, CilOpCodes.Ldnull, CilOpCodes.Cgt_Un, CilOpCodes.Ret }));

            foreach (var changed in new[]
                     {
                         shape! with { TypeInfoSlot = shape.OnceFlag },
                         shape! with { OnceFlag = shape.TypeInfoSlot },
                         shape! with { MetadataInitializer = method.UnderlyingPointer }
                     })
                Assert.That(X64BooleanParameterClassTestProof.BindProvedShape(method, changed), Is.Null);

            var bits = target.Definition.Bitfield;
            try
            {
                target.Definition.Bitfield = bits | (1u << 3);
                AssertRejected(method, body, definition, "Target static constructor effects remain outside this proof.");
            }
            finally { target.Definition.Bitfield = bits; }
            try
            {
                target.OverrideBaseType = target;
                AssertRejected(method, body, definition, "A changed hierarchy cannot be emitted from stale TypeInfo evidence.");
            }
            finally { target.OverrideBaseType = null; }
            try
            {
                target.OverrideAttributes = target.DefaultAttributes | TypeAttributes.Sealed;
                AssertRejected(method, body, definition, "The admitted target is unsealed and unchanged.");
            }
            finally { target.OverrideAttributes = null; }
            try
            {
                method.Parameters[0].OverrideParameterType = app.SystemTypes.SystemStringType;
                AssertRejected(method, body, definition, "The original source must be the unchanged object argument.");
            }
            finally { method.Parameters[0].OverrideParameterType = null; }
            try
            {
                method.OverrideReturnType = target;
                AssertRejected(method, body, definition, "A reference return does not share Boolean output semantics.");
            }
            finally { method.OverrideReturnType = null; }
            var aliases = app.MethodsByAddress[method.UnderlyingPointer];
            var originalBytes = method.RawBytes;
            try
            {
                var changedBytes = originalBytes.AsSpan().ToArray();
                changedBytes[0] ^= 1;
                method.RawBytes = new BinarySlice(changedBytes);
                AssertRejected(method, body, definition,
                    "Final emission cannot refresh away a changed nonempty native prefix.");
            }
            finally { method.RawBytes = originalBytes; }
            aliases.Add(method);
            try { AssertRejected(method, body, definition, "Ambiguous native ownership must prevent emission."); }
            finally { aliases.RemoveAt(aliases.Count - 1); }
            app.MethodsByAddress.Add(method.UnderlyingPointer + 1, [method]);
            try { AssertRejected(method, body, definition, "An interior managed entry invalidates the complete body."); }
            finally { app.MethodsByAddress.Remove(method.UnderlyingPointer + 1); }
            Assert.That(X64BooleanParameterClassTestRecovery.TryGenerate(method, definition), Is.True,
                "All metadata mutations must restore the positive control.");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerRejectsChangedExecutableBytesAndLoadedTargetOrReturnBytes()
    {
        var (binary, metadata) = InputPaths();
        var image = File.ReadAllBytes(binary);
        var metadataBytes = File.ReadAllBytes(metadata);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(image, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = SelectedMethod(app);
            var pe = (PE)app.Binary;
            var region = X64UnwindProof.ForApplication(app)!.ClassifySpan(
                method.UnderlyingPointer, method.UnderlyingPointer + 1);
            var body = X86Utils.Iterate(method).TakeWhile(instruction => instruction.IP < region.End).ToArray();
            var shape = X64BooleanParameterClassTestProof.TryProveShape(body)!;
            Assert.That(X64BooleanParameterClassTestProof.Find(method, body), Is.Not.Null);

            var changed = image.ToArray();
            changed[checked((int)pe.MapVirtualAddressToRaw(body[31].IP + 1, false))] = 0x94;
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
            var wrong = SelectedMethod(Cpp2IlApi.CurrentAppContext!);
            Assert.That(X64BooleanParameterClassTestProof.Find(wrong, body), Is.Null,
                "A stale decode cannot authenticate a changed Boolean condition.");
            Assert.That(X64BooleanParameterClassTestRecovery.TryGenerate(wrong, RejectedDefinition()), Is.False);

            foreach (var (address, length) in new[]
                     {
                         (method.UnderlyingPointer, 121), (shape.TypeInfoSlot, 8), (shape.OnceFlag, 1)
                     })
                foreach (var displacement in new[] { -7, 0, length - 1 })
                {
                    var relocated = WithIsolatedRelocation(pe, address, displacement);
                    Cpp2IlApi.ResetInternalState();
                    Cpp2IlApi.InitializeLibCpp2Il(relocated, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
                    var alteredApp = Cpp2IlApi.CurrentAppContext!;
                    var altered = SelectedMethod(alteredApp);
                    var unwind = X64UnwindProof.ForApplication(alteredApp)!;
                    var current = X86Utils.Iterate(altered).Take(35).ToArray();
                    Assert.Multiple(() =>
                    {
                        Assert.That(current, Is.EqualTo(body), "Relocation changes loaded bytes, not file decoding.");
                        Assert.That(X64BooleanParameterClassTestProof.TryProveShape(current), Is.Not.Null);
                        Assert.That(unwind.MatchesUnwind(region.Start, region.End, 6, 0, [6, 0x32, 2, 0x30]), Is.True);
                        Assert.That(X64PeOnceFlagProof.IsUnrelocatedRange((PE)alteredApp.Binary, unwind,
                            address, (uint)length), Is.False);
                        Assert.That(X64BooleanParameterClassTestProof.Find(altered, current), Is.Null);
                        Assert.That(X64BooleanParameterClassTestRecovery.TryGenerate(altered, RejectedDefinition()), Is.False);
                    });
                }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerRejectsLoaderChangesToItsBoundaryAndUnwindEvidence()
    {
        var (binary, metadata) = InputPaths();
        var metadataBytes = File.ReadAllBytes(metadata);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = SelectedMethod(app);
            var pe = (PE)app.Binary;
            var body = X64NativeInstructionReader.ReadRootBody(method)!;
            Assert.That(body, Has.Length.EqualTo(35));
            Assert.That(X64BooleanParameterClassTestProof.Find(method, body), Is.Not.Null);
            var targets = UnwindRelocationTargets(pe, method.UnderlyingPointer);
            Assert.That(targets.Select(target => target.Name), Does.Contain("function-table-record"));
            Assert.That(targets.Select(target => target.Name), Does.Contain("unwind-header"));
            Assert.That(targets.Select(target => target.Name), Does.Contain("unwind-codes"));
            foreach (var (name, address, length) in targets)
                foreach (var displacement in new[] { -7, 0, length - 1 })
                {
                    var changed = WithIsolatedRelocation(pe, address, displacement);
                    Cpp2IlApi.ResetInternalState();
                    Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
                    var altered = SelectedMethod(Cpp2IlApi.CurrentAppContext!);
                    var current = X86Utils.Iterate(altered).Take(35).ToArray();
                    Assert.Multiple(() =>
                    {
                        Assert.That(current, Is.EqualTo(body), "Relocation leaves the native method bytes unchanged.");
                        Assert.That(X64BooleanParameterClassTestProof.TryProveShape(current), Is.Not.Null);
                        Assert.That(X64NativeInstructionReader.ReadRootBody(altered), Is.Null,
                            $"Loaded {name} overlap invalidates the authoritative root boundary.");
                        Assert.That(X64BooleanParameterClassTestProof.Find(altered, current), Is.Null);
                        Assert.That(X64BooleanParameterClassTestRecovery.TryGenerate(altered, RejectedDefinition()), Is.False);
                    });
                }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerRejectsLoaderChangesToTypeInfoInitializerAndDispatchTable()
    {
        var (binary, metadata) = InputPaths();
        var metadataBytes = File.ReadAllBytes(metadata);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = SelectedMethod(app);
            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;
            var body = X64NativeInstructionReader.ReadRootBody(method)!;
            var shape = X64BooleanParameterClassTestProof.TryProveShape(body)!;
            Assert.That(X64MetadataInitializationHelperProof.TryIdentifyTypeInfo(app, pe, unwind,
                shape.MetadataInitializer), Is.True);
            Assert.That(X64BooleanParameterClassTestProof.Find(method, body), Is.Not.Null);
            var targets = MetadataRelocationTargets(pe, unwind, shape.MetadataInitializer);
            foreach (var (name, address, length) in targets)
                foreach (var displacement in new[] { -7, 0, length - 1 })
                {
                    var changed = WithIsolatedRelocation(pe, address, displacement);
                    Cpp2IlApi.ResetInternalState();
                    Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
                    var alteredApp = Cpp2IlApi.CurrentAppContext!;
                    var altered = SelectedMethod(alteredApp);
                    var alteredUnwind = X64UnwindProof.ForApplication(alteredApp)!;
                    var current = X64NativeInstructionReader.ReadRootBody(altered);
                    Assert.Multiple(() =>
                    {
                        Assert.That(current, Is.EqualTo(body),
                            "Loader overlap in a helper leaves the caller's bytes and authoritative boundary unchanged.");
                        Assert.That(X64BooleanParameterClassTestProof.TryProveShape(current!), Is.Not.Null);
                        Assert.That(X64PeOnceFlagProof.IsUnrelocatedRange((PE)alteredApp.Binary,
                            alteredUnwind, address, (uint)length), Is.False);
                        Assert.That(X64MetadataInitializationHelperProof.TryIdentifyTypeInfo(alteredApp,
                            (PE)alteredApp.Binary, alteredUnwind, shape.MetadataInitializer), Is.False,
                            $"Loaded {name} bytes cannot establish the TypeInfo helper contract.");
                        Assert.That(X64BooleanParameterClassTestProof.Find(altered, current!), Is.Null);
                        Assert.That(X64BooleanParameterClassTestRecovery.TryGenerate(altered, RejectedDefinition()), Is.False);
                    });
                }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static MethodDefinition RejectedDefinition()
    {
        var module = new ModuleDefinition("ClassTestProof.dll",
            new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        return new MethodDefinition("IsRemote", ManagedMethodAttributes.Public | ManagedMethodAttributes.Static,
            MethodSignature.CreateStatic(module.CorLibTypeFactory.Boolean, [module.CorLibTypeFactory.Object]));
    }

    private static List<(string Name, ulong Address, int Length)> MetadataRelocationTargets(
        PE pe, X64UnwindProof.Index unwind, ulong initializer)
    {
        var thunk = X64NativeInstructionReader.Read(pe, unwind, initializer, 1, 5)!;
        Assert.That(thunk[0].Mnemonic, Is.EqualTo(Mnemonic.Jmp));
        var wrapperAddress = thunk[0].NearBranchTarget;
        var wrapper = X64NativeInstructionReader.Read(pe, unwind, wrapperAddress, 2, 7)!;
        Assert.That(wrapper[1].Mnemonic, Is.EqualTo(Mnemonic.Jmp));
        var coreAddress = wrapper[1].NearBranchTarget;
        var coreSpan = unwind.ClassifySpan(coreAddress, coreAddress + 1);
        Assert.That(coreSpan.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
        Assert.That(coreSpan.Start, Is.EqualTo(coreAddress));
        // This exact fixture selects the independently authenticated 27-instruction
        // decoder prefix. Its size is read from .pdata, not a linked player address.
        Assert.That(coreSpan.End - coreAddress, Is.EqualTo(0x5d));
        var dispatch = X64NativeInstructionReader.Read(pe, unwind, coreSpan.End, 8, 0x23)!;
        var tableLoad = dispatch[1];
        Assert.Multiple(() =>
        {
            Assert.That(tableLoad.Mnemonic, Is.EqualTo(Mnemonic.Mov));
            Assert.That(tableLoad.Op0Register, Is.EqualTo(Register.EDX));
            Assert.That(tableLoad.MemoryBase, Is.EqualTo(Register.R8));
            Assert.That(tableLoad.MemoryIndex, Is.EqualTo(Register.RAX));
            Assert.That(tableLoad.MemoryIndexScale, Is.EqualTo(4));
            Assert.That(tableLoad.MemoryDisplacement64, Is.LessThanOrEqualTo(uint.MaxValue));
        });
        return
        [
            ("initializer-thunk", initializer, checked((int)(thunk[^1].NextIP - initializer))),
            ("throw-enabled-wrapper", wrapperAddress, checked((int)(wrapper[^1].NextIP - wrapperAddress))),
            ("metadata-decoder-core", coreAddress, checked((int)(coreSpan.End - coreAddress))),
            ("metadata-type-dispatch", coreSpan.End, checked((int)(dispatch[^1].NextIP - coreSpan.End))),
            ("typeinfo-switch-table", unwind.ImageBase + tableLoad.MemoryDisplacement64, 7 * sizeof(uint))
        ];
    }

    private static void AssertRejected(MethodAnalysisContext method, Instruction[] body,
        MethodDefinition definition, string reason)
    {
        Assert.That(X64BooleanParameterClassTestProof.Find(method, body), Is.Null, reason);
        Assert.That(X64BooleanParameterClassTestRecovery.TryGenerate(method, definition), Is.False, reason);
    }

    private static (string Binary, string Metadata) InputPaths()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_BOOLEAN_PARAMETER_CLASS_TEST_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_BOOLEAN_PARAMETER_CLASS_TEST_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        return (binary, metadata);
    }

    private static MethodAnalysisContext SelectedMethod(ApplicationAnalysisContext app)
    {
        var method = app.GetAssemblyByName("BooleanParameterClassTestFixture")!.Types.SelectMany(type => type.Methods)
            .Single(method => method.Name == "IsRemote");
        method.EnsureRawBytes();
        return method;
    }

    private static List<(string Name, ulong Address, int Length)> UnwindRelocationTargets(
        PE pe, ulong methodAddress)
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
            var unwindRva = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8, 4));
            var unwindAddress = imageBase + unwindRva;
            var unwindRaw = checked((int)pe.MapVirtualAddressToRaw(unwindAddress, false));
            var count = image[unwindRaw + 2];
            var targets = new List<(string, ulong, int)>
            {
                ("function-table-record", imageBase + rva + (uint)offset, 12),
                ("unwind-header", unwindAddress, 4),
                ("unwind-codes", unwindAddress + 4, 2 * count)
            };
            // An odd slot count has two real alignment bytes. This fixture may
            // have none; the general parser's synthetic controls cover that case.
            if ((count & 1) != 0)
                targets.Add(("unwind-padding", unwindAddress + 4 + 2UL * count, 2));
            return targets;
        }
        throw new AssertionException("The selected exact method must own a function-table record.");
    }

    // A valid single DIR64 block isolates overlap without changing native bytes,
    // section mappings, method registrations or .pdata/.xdata ownership evidence.
    private static byte[] WithIsolatedRelocation(PE pe, ulong address, int displacement)
    {
        var image = pe.GetRawBinaryContent().ToArray();
        var header = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x3C, 4)));
        var optional = header + 24;
        var imageBase = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(optional + 24, 8));
        var directory = optional + 112 + 5 * 8;
        var relocationRva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(directory, 4));
        var raw = checked((int)pe.MapVirtualAddressToRaw(imageBase + relocationRva, false));
        var rva = checked((uint)((long)address + displacement - (long)imageBase));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(directory + 4, 4), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(raw, 4), rva & ~0xFFFU);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(raw + 4, 4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(raw + 8, 2), (ushort)(0xA000U | (rva & 0xFFFU)));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(raw + 10, 2), 0);
        return image;
    }

    // Individually assembled public synthetic instructions use neutral addresses.
    // RIP slots and branch edges are bound after decoding, rather than retaining
    // a player body, linked displacements or a binary fingerprint.
    private static Instruction[] CreateNativeBody()
    {
        string[] fragments =
        [
            "4053", "4883EC20", "803D0000000000", "488BD9", "7500",
            "488D0D00000000", "E800000000", "C6050000000001", "4885DB", "7500",
            "33C0", "4885C0", "0F95C0", "4883C420", "5B", "C3",
            "488B1500000000", "488B03", "0FB68A2C010000", "38882C010000", "7200",
            "488B80C8000000", "483954C8F8", "7500", "B101", "EB00", "32C9",
            "33C0", "84C9", "480F45C3", "4885C0", "0F95C0", "4883C420", "5B", "C3"
        ];
        var body = new Instruction[fragments.Length];
        var address = 0x1000UL;
        for (var index = 0; index < fragments.Length; index++)
        {
            body[index] = Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(fragments[index])), address).Decode();
            address = body[index].NextIP;
        }
        body[2].MemoryDisplacement64 = body[7].MemoryDisplacement64 = 0x4000;
        body[5].MemoryDisplacement64 = body[16].MemoryDisplacement64 = 0x3000;
        body[6].NearBranch64 = 0x5000;
        body[4].NearBranch64 = body[8].IP;
        body[9].NearBranch64 = body[16].IP;
        body[20].NearBranch64 = body[23].NearBranch64 = body[26].IP;
        body[25].NearBranch64 = body[27].IP;
        return body;
    }
}
