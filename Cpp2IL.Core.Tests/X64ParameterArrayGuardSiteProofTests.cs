using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;
using Classification = Cpp2IL.Core.InstructionSets.X64UnwindProof.SpanClassification;
using SpanKind = Cpp2IL.Core.InstructionSets.X64UnwindProof.SpanKind;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64ParameterArrayGuardSiteProofTests
{
    [Test]
    public void CompleteTwoParameterSitesPreserveSharedOffsetAndOrderedCall()
    {
        var body = SyntheticBody();
        var proof = Prove(body);
        Assert.That(proof, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(proof!.FirstElementRead, Is.EqualTo(12));
            Assert.That(proof.SecondElementRead, Is.EqualTo(19));
            Assert.That(proof.IndexExtension, Is.EqualTo(5));
            Assert.That(proof.OffsetLea, Is.EqualTo(11));
            Assert.That(proof.ArrayCapture, Is.EqualTo(6));
            Assert.That(proof.MarkCall, Is.EqualTo(14));
            Assert.That(proof.NullHelperCall, Is.EqualTo(27));
            Assert.That(proof.BoundsHelperCall, Is.EqualTo(29));
        });
    }

    [TestCase(true, false, false, false)]
    [TestCase(false, true, false, false)]
    [TestCase(false, false, true, false)]
    [TestCase(false, false, false, true)]
    public void WrongCaptureOffsetElementOrGuardIsRejected(bool wrongCapture,
        bool wrongOffsetScale, bool wrongSecondArray, bool wrongBoundsExit)
    {
        var body = SyntheticBody(wrongCapture, wrongOffsetScale,
            wrongSecondArray, wrongBoundsExit);
        Assert.That(Prove(body), Is.Null);
    }

    [Test]
    public void UnprovedHelperOrNativeRegionCannotCloseTheCaller()
    {
        var body = SyntheticBody();
        Assert.That(X64ArrayGuardSiteProof.TryProveParameterNative(body,
            body[0].IP, CompleteRegion(body), _ => false, _ => true),
            Is.Null);
        Assert.That(X64ArrayGuardSiteProof.TryProveParameterNative(body,
            body[0].IP, (_, _) => new Classification(SpanKind.HandlerFree,
                body[0].IP, body[^1].NextIP + 1, body[0].IP),
            _ => true, _ => true), Is.Null);
    }

    [Test]
    public void ExactOriginalPlayerBindsDistinctArrayParametersAndIndex()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_PARAMETER_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_PARAMETER_ARRAY_FIXTURE_INPUT to the neutral fixture player-input directory.");
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
            var method = app.GetAssemblyByName("ParameterArrayFixture")!.Types
                .SelectMany(type => type.Methods)
                .Single(candidate => candidate.Name == "CompareWithMark");
            var proof = X64ArrayGuardSiteProof.FindParameter(method,
                X86Utils.Iterate(method).ToArray());
            Assert.That(proof, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(proof!.Sites, Has.Count.EqualTo(2));
                Assert.That(proof.Sites[0].ArrayParameter,
                    Is.Not.SameAs(proof.Sites[1].ArrayParameter));
                Assert.That(proof.Sites[0].ArrayEntryRegister,
                    Is.EqualTo(Register.RDX));
                Assert.That(proof.Sites[1].ArrayEntryRegister,
                    Is.EqualTo(Register.R8));
                Assert.That(proof.Sites[1].ArrayUseRegister,
                    Is.EqualTo(Register.RDI));
                Assert.That(proof.Sites[1].ArrayCaptureIps,
                    Has.Count.EqualTo(1));
                Assert.That(proof.IndexEntryRegister,
                    Is.EqualTo(Register.R9));
                Assert.That(proof.IndexUseRegister,
                    Is.EqualTo(Register.RBX));
                Assert.That(proof.OffsetRegister,
                    Is.EqualTo(Register.RSI));
                Assert.That(proof.Sites[1].EffectsSincePreviousAccess,
                    Has.Count.EqualTo(1));
                Assert.That(proof.Sites[1].EffectsSincePreviousAccess[0].Kind,
                    Is.EqualTo(X64ArrayGuardSiteProof.EffectKind.DirectCall));
            });

            var native = X86Utils.Iterate(method).ToArray();
            method.OverrideReturnType = app.SystemTypes.SystemBooleanType;
            Assert.That(X64ArrayGuardSiteProof.FindParameter(method, native),
                Is.Null, "An effective Boolean override cannot replace raw return metadata.");
            method.OverrideReturnType = null;

            method.OverrideAttributes = method.DefaultAttributes ^
                System.Reflection.MethodAttributes.Final;
            Assert.That(X64ArrayGuardSiteProof.FindParameter(method, native),
                Is.Null, "A changed managed declaration cannot reuse this native proof.");
            method.OverrideAttributes = null;

            var target = app.GetAssemblyByName("ParameterArrayFixture")!.Types
                .SelectMany(type => type.Methods)
                .Single(candidate => candidate.Name == "Mark");
            target.OverrideReturnType = app.SystemTypes.SystemVoidType;
            Assert.That(X64ArrayGuardSiteProof.FindParameter(method, native),
                Is.Null, "The intervening call must retain its raw void signature.");
            target.OverrideReturnType = null;
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }

    private static X64ArrayGuardSiteProof.NativeParameterEvidence? Prove(
        IReadOnlyList<Instruction> body) =>
        X64ArrayGuardSiteProof.TryProveParameterNative(body,
            body[0].IP, CompleteRegion(body), _ => true, _ => true);

    private static Func<ulong, ulong, Classification> CompleteRegion(
        IReadOnlyList<Instruction> body) => (_, _) =>
        new Classification(SpanKind.HandlerFree, body[0].IP,
            body[^1].NextIP, body[0].IP);

    private static Instruction[] SyntheticBody(bool wrongCapture = false,
        bool wrongOffsetScale = false, bool wrongSecondArray = false,
        bool wrongBoundsExit = false)
    {
        const ulong entry = 0x1000;
        var assembler = new Assembler(64);
        var nullExit = assembler.CreateLabel();
        var boundsExit = assembler.CreateLabel();
        assembler.mov(__qword_ptr[rsp + 8], rbx);
        assembler.mov(__qword_ptr[rsp + 0x10], rbp);
        assembler.mov(__qword_ptr[rsp + 0x18], rsi);
        assembler.push(rdi);
        assembler.sub(rsp, 0x20);
        assembler.movsxd(rbx, r9d);
        assembler.mov(rdi, wrongCapture ? rcx : r8);
        assembler.test(rdx, rdx);
        assembler.je(nullExit);
        assembler.cmp(ebx, __dword_ptr[rdx + 0x18]);
        assembler.jae(boundsExit);
        assembler.lea(rsi, __qword_ptr[rbx * (wrongOffsetScale ? 4 : 8) + 0x20]);
        assembler.mov(rbp, __qword_ptr[rsi + rdx]);
        assembler.xor(edx, edx);
        assembler.call(0x4000UL);
        assembler.test(rdi, rdi);
        assembler.je(nullExit);
        assembler.cmp(ebx, __dword_ptr[rdi + 0x18]);
        if (wrongBoundsExit)
            assembler.jae(nullExit);
        else
            assembler.jae(boundsExit);
        if (wrongSecondArray)
            assembler.cmp(rbp, __qword_ptr[rsi + rdx]);
        else
            assembler.cmp(rbp, __qword_ptr[rsi + rdi]);
        assembler.mov(rbx, __qword_ptr[rsp + 0x30]);
        assembler.mov(rbp, __qword_ptr[rsp + 0x38]);
        assembler.sete(al);
        assembler.mov(rsi, __qword_ptr[rsp + 0x40]);
        assembler.add(rsp, 0x20);
        assembler.pop(rdi);
        assembler.ret();
        assembler.Label(ref nullExit);
        assembler.call(0x5000UL);
        assembler.int3();
        assembler.Label(ref boundsExit);
        assembler.call(0x6000UL);
        assembler.int3();

        using var stream = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(stream), entry);
        return X86Utils.Iterate(stream.ToArray().AsSpan(), entry, false).ToArray();
    }
}
