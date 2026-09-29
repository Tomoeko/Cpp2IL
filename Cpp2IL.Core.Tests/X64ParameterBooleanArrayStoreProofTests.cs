using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64ParameterBooleanArrayStoreProofTests
{
    [TestCase(false, -1)]
    [TestCase(false, 0)]
    [TestCase(false, 1)]
    [TestCase(true, -1)]
    [TestCase(true, 0)]
    [TestCase(true, 1)]
    public void CompleteParameterAndCanonicalLiteralStoresBindActualAbiSlots(bool instance, int literal)
    {
        var proof = X64ParameterBooleanArrayStoreProof.TryProveShape(Body(instance, literal));
        Assert.That(proof, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(proof!.ArrayEntry, Is.EqualTo(instance ? NativeRegister.RDX : NativeRegister.RCX));
            Assert.That(proof.IndexEntry, Is.EqualTo(instance ? NativeRegister.R8 : NativeRegister.RDX));
            Assert.That(proof.ValueEntry, Is.EqualTo(literal < 0
                ? instance ? NativeRegister.R9 : NativeRegister.R8 : NativeRegister.None));
            Assert.That(proof.Literal, Is.EqualTo(literal < 0 ? (int?)null : literal));
            Assert.That(proof.InstructionCount, Is.EqualTo(12));
        });
    }

    [TestCase("capture")]
    [TestCase("early-extension")]
    [TestCase("copied-value")]
    [TestCase("register-zero")]
    public void PureRegisterSetupPreservesTheProvedOrigins(string variant)
    {
        var proof = X64ParameterBooleanArrayStoreProof.TryProveShape(Body(variant: variant));
        Assert.That(proof, Is.Not.Null);
        Assert.That(proof!.InstructionCount, Is.EqualTo(13));
        Assert.That(proof.ArrayEntry, Is.EqualTo(NativeRegister.RCX));
        Assert.That(proof.IndexEntry, Is.EqualTo(NativeRegister.RDX));
        Assert.That(proof.Literal, Is.EqualTo(variant == "register-zero" ? 0 : (int?)null));
    }

    [TestCase("signed-bounds")]
    [TestCase("bounds-to-null")]
    [TestCase("null-to-bounds")]
    [TestCase("wrong-array")]
    [TestCase("array-replaced-after-null")]
    [TestCase("wrong-index")]
    [TestCase("zero-extension")]
    [TestCase("wide-store")]
    [TestCase("wrong-scale")]
    [TestCase("wrong-offset")]
    [TestCase("literal-two")]
    [TestCase("prior-call")]
    [TestCase("prior-store")]
    [TestCase("missing-trap")]
    [TestCase("nonvolatile-capture")]
    public void IncompleteGuardsAliasedValuesAndAdditionalEffectsAreRejected(string variant)
    {
        Assert.That(X64ParameterBooleanArrayStoreProof.TryProveShape(Body(variant: variant)), Is.Null);
    }

    [Test]
    public void InteriorDiscontinuityOrInstructionPrefixCannotProveACompleteBody()
    {
        var body = Body();
        var changed = body[6];
        changed.IP++;
        body[6] = changed;
        Assert.That(X64ParameterBooleanArrayStoreProof.TryProveShape(body), Is.Null);
        body = Body();
        changed = body[6];
        changed.HasLockPrefix = true;
        body[6] = changed;
        Assert.That(X64ParameterBooleanArrayStoreProof.TryProveShape(body), Is.Null);
    }

    [Test]
    public void ExactPlayerProofAndFinalEmissionRejectChangedTypesOriginsAndEffects()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_PARAMETER_BOOLEAN_ARRAY_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_PARAMETER_BOOLEAN_ARRAY_STORE_FIXTURE_INPUT to the neutral exact player input.");
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
            var methods = app.GetAssemblyByName("ParameterBooleanArrayStoreFixture")!.Types
                .SelectMany(type => type.Methods).Where(method => method.Name != ".ctor").ToArray();
            Assert.That(methods, Has.Length.EqualTo(9));
            foreach (var method in methods)
            {
                method.EnsureRawBytes();
                var native = X86Utils.Iterate(method).ToArray();
                var proof = X64ParameterBooleanArrayStoreProof.Find(method, native);
                Assert.That(proof, Is.Not.Null, method.Name);
                var array = proof!.ArrayParameter;
                try
                {
                    array.OverrideParameterType = new SzArrayTypeAnalysisContext(app.SystemTypes.SystemByteType);
                    Assert.That(X64ParameterBooleanArrayStoreProof.Find(method, native), Is.Null);
                }
                finally { array.OverrideParameterType = null; }
                var rawElement = array.Definition!.RawType!.GetEncapsulatedType();
                var originalMods = rawElement.NumMods;
                try
                {
                    rawElement.NumMods = 1;
                    Assert.That(X64ParameterBooleanArrayStoreProof.Find(method, native), Is.Null,
                        "A resolved Boolean wrapper cannot hide modified raw element metadata.");
                }
                finally { rawElement.NumMods = originalMods; }
                if (proof.ValueParameter is { } parameter)
                {
                    try
                    {
                        parameter.OverrideParameterType = app.SystemTypes.SystemByteType;
                        Assert.That(X64ParameterBooleanArrayStoreProof.Find(method, native), Is.Null);
                    }
                    finally { parameter.OverrideParameterType = null; }
                }
                var region = X64UnwindProof.ForApplication(app)!.ClassifySpan(method.UnderlyingPointer, method.UnderlyingPointer + 1);
                var interior = method.UnderlyingPointer + 1;
                try
                {
                    app.MethodsByAddress.Add(interior, [method]);
                    Assert.That(X64ParameterBooleanArrayStoreProof.Find(method, native), Is.Null);
                }
                finally { app.MethodsByAddress.Remove(interior); }
                Assert.That(region.End, Is.GreaterThan(method.UnderlyingPointer));

                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty);
                IlGenerator.ValidateParameterBooleanArrayStores(method);
                if (!method.IsStatic)
                {
                    var receiver = new Cpp2IL.Core.ISIL.Register(null, "rcx");
                    Assert.That(X86UnusedReceiverProof.IsUnused(method, receiver), Is.True);
                    Assert.That(X86UnusedReceiverProof.IsUnused(method,
                        new Cpp2IL.Core.ISIL.Register(receiver.Number + 1, receiver.Name)), Is.False);
                    var incoming = method.ParameterOperands[0];
                    try
                    {
                        method.ParameterOperands[0] = new Cpp2IL.Core.ISIL.Register(null, "rdx");
                        Assert.That(X86UnusedReceiverProof.IsUnused(method, receiver), Is.False);
                    }
                    finally { method.ParameterOperands[0] = incoming; }
                    var originalBytes = method.RawBytes;
                    try
                    {
                        var changed = originalBytes.AsSpan().ToArray();
                        changed[0] ^= 1;
                        method.RawBytes = new BinarySlice(changed);
                        Assert.That(X86UnusedReceiverProof.IsUnused(method, receiver), Is.False,
                            "A retained store marker cannot hide changed native evidence.");
                    }
                    finally { method.RawBytes = originalBytes; }
                }
                var definition = method.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!;
                IlGenerator.GenerateIl(method, definition);
                Assert.That(definition.CilMethodBody!.Instructions.Count(instruction =>
                    instruction.OpCode == AsmResolver.PE.DotNet.Cil.CilOpCodes.Stelem_I1), Is.EqualTo(1));
                var store = method.ControlFlowGraph!.Instructions.Single(instruction => instruction.OpCode == OpCode.Move);
                var access = (ArrayAccess)store.Operands[0];
                var originalIndex = access.Index;
                try
                {
                    access.Index = new Immediate(0);
                    RejectEmission(method, definition);
                }
                finally { access.Index = originalIndex; }
                var originalValue = store.Operands[1];
                try
                {
                    store.SetOperand(1, new Immediate(2));
                    RejectEmission(method, definition);
                }
                finally { store.SetOperand(1, originalValue); }
                var block = method.ControlFlowGraph.FindBlockByInstruction(store)!;
                var priorCall = new ManagedInstruction(-1, OpCode.CallVoid, method);
                try
                {
                    block.Instructions.Insert(0, priorCall);
                    RejectEmission(method, definition);
                }
                finally { block.Instructions.Remove(priorCall); }
                var originalAddress = store.NativeAddress;
                try
                {
                    store.NativeAddress = originalAddress + 1;
                    RejectEmission(method, definition);
                }
                finally { store.NativeAddress = originalAddress; }
                IlGenerator.ValidateParameterBooleanArrayStores(method);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectEmission(MethodAnalysisContext method, AsmResolver.DotNet.MethodDefinition definition) =>
        Assert.That(() => IlGenerator.GenerateIl(method, definition),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Parameter Boolean-array store proof"));

    private static NativeInstruction[] Body(bool instance = false, int literal = -1, string variant = "")
    {
        const ulong entry = 0x1000;
        var assembler = new Assembler(64);
        var nullExit = assembler.CreateLabel();
        var boundsExit = assembler.CreateLabel();
        assembler.sub(rsp, 0x28);
        if (variant == "prior-call") assembler.call(0x6000UL);
        if (variant == "prior-store") assembler.mov(__byte_ptr[rcx + 0x10], 1);
        if (variant == "nonvolatile-capture") assembler.mov(rbx, rcx);
        if (variant == "capture") assembler.mov(r10, rcx);
        if (variant == "copied-value") assembler.mov(r11, r8);
        if (variant == "early-extension") assembler.movsxd(r10, edx);
        if (variant == "register-zero") assembler.xor(r11d, r11d);
        if (instance) assembler.test(rdx, rdx);
        else if (variant == "capture") assembler.test(r10, r10);
        else assembler.test(rcx, rcx);
        assembler.je(variant == "null-to-bounds" ? boundsExit : nullExit);
        if (variant == "array-replaced-after-null") assembler.mov(rcx, r9);
        if (instance) assembler.cmp(r8d, __dword_ptr[rdx + 0x18]);
        else if (variant == "capture") assembler.cmp(edx, __dword_ptr[r10 + 0x18]);
        else assembler.cmp(edx, __dword_ptr[rcx + 0x18]);
        if (variant == "signed-bounds") assembler.jge(boundsExit);
        else assembler.jae(variant == "bounds-to-null" ? nullExit : boundsExit);
        if (variant == "zero-extension") assembler.mov(eax, edx);
        else if (variant == "early-extension") assembler.movsxd(rax, r10d);
        else if (instance) assembler.movsxd(rax, r8d);
        else assembler.movsxd(rax, variant == "wrong-index" ? r9d : edx);
        var offset = variant == "wrong-offset" ? 0x21 : 0x20;
        if (variant == "wide-store") assembler.mov(__dword_ptr[rcx + rax + offset], r8d);
        else if (variant == "wrong-scale") assembler.mov(__byte_ptr[rcx + rax * 2 + offset], r8b);
        else if (variant == "wrong-array") assembler.mov(__byte_ptr[rdx + rax + offset], r8b);
        else if (variant == "capture") assembler.mov(__byte_ptr[r10 + rax + offset], r8b);
        else if (variant == "copied-value" || variant == "register-zero") assembler.mov(__byte_ptr[rcx + rax + offset], r11b);
        else if (variant == "literal-two") assembler.mov(__byte_ptr[rcx + rax + offset], 2);
        else if (instance && literal >= 0) assembler.mov(__byte_ptr[rdx + rax + offset], literal);
        else if (instance) assembler.mov(__byte_ptr[rdx + rax + offset], r9b);
        else if (literal >= 0) assembler.mov(__byte_ptr[rcx + rax + offset], literal);
        else assembler.mov(__byte_ptr[rcx + rax + offset], r8b);
        assembler.add(rsp, 0x28);
        assembler.ret();
        assembler.Label(ref nullExit);
        assembler.call(0x4000UL);
        if (variant != "missing-trap") assembler.int3();
        assembler.Label(ref boundsExit);
        assembler.call(0x5000UL);
        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), entry);
        return X86Utils.Iterate(stream.ToArray().AsSpan(), entry, false).ToArray();
    }
}
