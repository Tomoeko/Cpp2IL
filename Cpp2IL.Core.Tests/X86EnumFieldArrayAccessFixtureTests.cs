using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;
using ArrayAccess = Cpp2IL.Core.ISIL.ArrayAccess;
using Immediate = Cpp2IL.Core.ISIL.Immediate;
using IsilOpCode = Cpp2IL.Core.ISIL.OpCode;
using LocalVariable = Cpp2IL.Core.ISIL.LocalVariable;
using MemoryOperand = Cpp2IL.Core.ISIL.MemoryOperand;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X86EnumFieldArrayAccessFixtureTests
{
    [TestCase(false, 0, 0)]
    [TestCase(false, 2, 1)]
    [TestCase(true, 0, 2)]
    public void FourByteReadsKeepTheirIndexContract(bool indexed, int fixedIndex,
        int kind)
    {
        var body = CreateBody(indexed, fixedIndex);
        Assert.That(X86FieldArrayAccessProof.TryProveShape(body), Is.EqualTo(
            new X86FieldArrayAccessProof.Shape((X86FieldArrayAccessProof.AccessKind)kind, 0x18,
                indexed ? 10 : 9, indexed ? 12 : 11, fixedIndex)));
    }

    [TestCase("owner")]
    [TestCase("field-range")]
    [TestCase("null-source")]
    [TestCase("null-exit")]
    [TestCase("signed-bounds")]
    [TestCase("bounds-exit")]
    [TestCase("element-width")]
    [TestCase("element-address")]
    [TestCase("fixed-index")]
    [TestCase("stack-allocation")]
    [TestCase("stack-restoration")]
    [TestCase("return")]
    [TestCase("null-trap")]
    [TestCase("indirect-helper")]
    [TestCase("discontinuous")]
    [TestCase("non64bit")]
    [TestCase("lock-prefix")]
    [TestCase("segment-prefix")]
    public void ChangedNativeAccessOrFailurePathRemainsUnproved(string mutation)
    {
        var body = CreateBody(false, 2);
        switch (mutation)
        {
            case "owner": body[1].MemoryBase = Register.RDX; break;
            case "field-range": body[1].MemoryDisplacement64 = 8; break;
            case "null-source": body[2].Op1Register = Register.RDX; break;
            case "null-exit": body[3].NearBranch64 = body[11].IP; break;
            case "signed-bounds": body[5].Code = Code.Jle_rel8_64; break;
            case "bounds-exit": body[5].NearBranch64 = body[9].IP; break;
            case "element-width": body[6].Code = Code.Mov_r64_rm64; break;
            case "element-address": body[6].MemoryDisplacement64 += 4; break;
            case "fixed-index": body[4].Immediate8 = 3; break;
            case "stack-allocation": body[0].Immediate8 = 0x20; break;
            case "stack-restoration": body[7].Immediate8 = 0x20; break;
            case "return": body[8].Code = Code.Retnq_imm16; break;
            case "null-trap": body[10].Code = Code.Nopd; break;
            case "indirect-helper": body[11].Code = Code.Call_rm64; break;
            case "discontinuous": body[6].IP++; break;
            case "non64bit": body[6].CodeSize = CodeSize.Code32; break;
            case "lock-prefix": body[6].HasLockPrefix = true; break;
            case "segment-prefix": body[6].SegmentPrefix = Register.FS; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.That(X86FieldArrayAccessProof.TryProveShape(body), Is.Null);
    }

    [TestCase("index-extension")]
    [TestCase("index-source")]
    [TestCase("element-stride")]
    public void ChangedSignedIndexAddressRemainsUnproved(string mutation)
    {
        var body = CreateBody(true, 0);
        switch (mutation)
        {
            case "index-extension": body[6].Code = Code.Mov_r32_rm32; break;
            case "index-source": body[6].Op1Register = Register.R8D; break;
            case "element-stride": body[7].MemoryIndexScale = 8; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.That(X86FieldArrayAccessProof.TryProveShape(body), Is.Null);
    }

    [Test]
    public void ExactPlayerRequiresEnumIdentityBackingStorageAndBothHelpers()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ENUM_FIELD_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ENUM_FIELD_ARRAY_FIXTURE_INPUT to the neutral exact player input.");
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
            var assembly = app.GetAssemblyByName("EnumFieldArrayFixture")!;
            foreach (var ownerName in new[] { "SignedReader", "UnsignedReader" })
            {
                var owner = assembly.Types.Single(type => type.Name == ownerName);
                var array = owner.Fields.Single(field => field.Name == "Values");
                foreach (var name in new[] { "ReadFirst", "ReadFixed", "ReadAt" })
                {
                    var method = owner.Methods.Single(candidate => candidate.Name == name);
                    var native = X86Utils.Iterate(method).ToArray();
                    var evidence = X86FieldArrayAccessProof.Find(method, native);
                    Assert.That(evidence, Is.Not.Null, ownerName + "." + name);
                    Assert.That(evidence!.Field, Is.SameAs(array));
                    Assert.That(((SzArrayTypeAnalysisContext)array.FieldType).ElementType,
                        Is.SameAs(method.ReturnType));
                    Assert.That(Enum32StorageProof.IsUnchanged(method.ReturnType), Is.True);
                    Assert.That(method.ReturnType.EnumUnderlyingType, Is.SameAs(
                        ownerName == "SignedReader" ? app.SystemTypes.SystemInt32Type :
                            app.SystemTypes.SystemUInt32Type));
                    Assert.That(app.MethodsByAddress[method.UnderlyingPointer], Has.Count.EqualTo(1));
                    var lifted = X86FieldArrayAccessProof.TryLift(method, native)!;
                    Assert.That(lifted.Select(instruction => instruction.OpCode), Is.EqualTo(new[]
                        { IsilOpCode.Move, IsilOpCode.Move, IsilOpCode.Return }));
                    Assert.That(lifted[1].NativeAddress, Is.EqualTo(evidence.ElementReadAddress));
                    Assert.That(lifted[1].IntegerBitWidth, Is.EqualTo(32));
                    method.Analyze();
                    var read = method.ControlFlowGraph!.Instructions.Single(instruction => instruction is
                        { OpCode: IsilOpCode.Move, Operands: [_, ArrayAccess] });
                    var access = (ArrayAccess)read.Operands[1];
                    var expectedIndex = name == "ReadFirst" ? 0 : 2;
                    Assert.That(name == "ReadAt" ? access.Index is LocalVariable :
                        access.Index is Immediate { Value: var value } && value == expectedIndex, Is.True);
                    var memory = new MemoryOperand(access.Array,
                        name == "ReadAt" ? access.Index : null,
                        name == "ReadAt" ? 0x20 : 0x20 + expectedIndex * 4,
                        name == "ReadAt" ? 4 : 0);
                    var arrayLocal = (LocalVariable)access.Array;
                    try
                    {
                        read.SetOperand(1, memory);
                        Assert.That(X86FieldArrayAccessProof.IsProvedEnumRead(method, read, 1,
                            memory, arrayLocal, method.ReturnType), Is.True);
                        read.NativeAddress++;
                        Assert.That(X86FieldArrayAccessProof.IsProvedEnumRead(method, read, 1,
                            memory, arrayLocal, method.ReturnType), Is.False);
                        read.NativeAddress = evidence.ElementReadAddress;
                        read.IntegerBitWidth = 8;
                        Assert.That(X86FieldArrayAccessProof.IsProvedEnumRead(method, read, 1,
                            memory, arrayLocal, method.ReturnType), Is.False);
                        read.IntegerBitWidth = 32;
                        Assert.That(X86FieldArrayAccessProof.IsProvedEnumRead(method, read, 0,
                            memory, arrayLocal, method.ReturnType), Is.False,
                            "An enum store does not inherit a read contract.");
                        memory.Addend += 4;
                        Assert.That(X86FieldArrayAccessProof.IsProvedEnumRead(method, read, 1,
                            memory, arrayLocal, method.ReturnType), Is.False);
                    }
                    finally
                    {
                        read.NativeAddress = evidence.ElementReadAddress;
                        read.IntegerBitWidth = 32;
                        read.SetOperand(1, access);
                    }

                    var element = method.ReturnType;
                    var backing = element.Fields.Single(field => !field.IsStatic);
                    RejectMutation(method, native, () => element.OverrideEnumUnderlyingType =
                        app.SystemTypes.SystemInt64Type, () => element.OverrideEnumUnderlyingType = null);
                    RejectMutation(method, native, () => element.OverrideEnumUnderlyingType =
                        app.SystemTypes.SystemUInt32Type, () => element.OverrideEnumUnderlyingType = null);
                    RejectMutation(method, native, () => element.OverrideAttributes =
                        element.DefaultAttributes | TypeAttributes.ExplicitLayout,
                        () => element.OverrideAttributes = null);
                    RejectMutation(method, native, () => backing.OverrideFieldType =
                        app.SystemTypes.SystemInt64Type, () => backing.OverrideFieldType = null);
                    RejectMutation(method, native, () => backing.OverrideOffset = 4,
                        () => backing.OverrideOffset = null);
                    RejectMutation(method, native, () => backing.OverrideAttributes =
                        backing.DefaultAttributes & ~FieldAttributes.RTSpecialName,
                        () => backing.OverrideAttributes = null);
                    RejectMutation(method, native, () => array.OverrideOffset = array.DefaultOffset + 8,
                        () => array.OverrideOffset = null);
                    var other = assembly.Types.Single(type => type.Name ==
                        (ownerName == "SignedReader" ? "UnsignedTone" : "SignedTone"));
                    RejectMutation(method, native, () => array.OverrideFieldType =
                        new SzArrayTypeAnalysisContext(other), () => array.OverrideFieldType = null);
                    RejectMutation(method, native, () => method.OverrideReturnType = other,
                        () => method.OverrideReturnType = null);
                }
            }

            var selected = assembly.Types.Single(type => type.Name == "SignedReader")
                .Methods.Single(method => method.Name == "ReadAt");
            var originalBody = X86Utils.Iterate(selected).ToArray();
            var shape = X86FieldArrayAccessProof.TryProveShape(originalBody)!;
            var region = X86ScalarArrayAccessProof.TryCompleteTrapTerminatedRegion(app,
                selected.UnderlyingPointer, originalBody, shape.BoundsCallIndex)!;
            var pe = (PE)app.Binary;
            var image = File.ReadAllBytes(binary);
            var metadataBytes = File.ReadAllBytes(metadata);
            foreach (var instruction in new[] { region[3], region[5], region[7],
                         region[shape.NullCallIndex], region[shape.BoundsCallIndex], region[^1] })
            {
                var changed = (byte[])image.Clone();
                changed[checked((int)pe.MapVirtualAddressToRaw(instruction.NextIP - 1, false))] ^= 1;
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(changed, metadataBytes, UnityVersion.Parse("2021.3.35f1"));
                var altered = Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("EnumFieldArrayFixture")!
                    .Types.Single(type => type.Name == "SignedReader").Methods
                    .Single(method => method.Name == "ReadAt");
                Assert.That(X86FieldArrayAccessProof.Find(altered, X86Utils.Iterate(altered).ToArray()),
                    Is.Null, "A changed guard, helper, element or trap must remain unproved.");
                Assert.That(X86FieldArrayAccessProof.Find(altered, originalBody), Is.Null,
                    "Cached native instructions must agree with the file-backed bytes.");
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectMutation(MethodAnalysisContext method, Instruction[] native,
        Action mutate, Action restore)
    {
        try
        {
            mutate();
            Assert.That(X86FieldArrayAccessProof.Find(method, native), Is.Null);
        }
        finally { restore(); }
    }

    private static Instruction[] CreateBody(bool indexed, int fixedIndex)
    {
        var assembler = new Assembler(64);
        var nullExit = assembler.CreateLabel();
        var boundsExit = assembler.CreateLabel();
        assembler.sub(rsp, 0x28);
        if (indexed)
        {
            assembler.mov(r8, __qword_ptr[rcx + 0x18]);
            assembler.test(r8, r8);
            assembler.je(nullExit);
            assembler.cmp(edx, __dword_ptr[r8 + 0x18]);
            assembler.jae(boundsExit);
            assembler.movsxd(rax, edx);
            assembler.mov(eax, __dword_ptr[r8 + rax * 4 + 0x20]);
        }
        else
        {
            assembler.mov(rax, __qword_ptr[rcx + 0x18]);
            assembler.test(rax, rax);
            assembler.je(nullExit);
            assembler.cmp(__dword_ptr[rax + 0x18], fixedIndex);
            assembler.jbe(boundsExit);
            assembler.mov(eax, __dword_ptr[rax + 0x20 + fixedIndex * 4]);
        }
        assembler.add(rsp, 0x28);
        assembler.ret();
        assembler.Label(ref nullExit);
        assembler.call(0x2000UL);
        assembler.int3();
        assembler.Label(ref boundsExit);
        assembler.call(0x3000UL);
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), 0x1000);
        return X86Utils.Disassemble(stream.ToArray(), 0x1000, false).ToArray();
    }
}
