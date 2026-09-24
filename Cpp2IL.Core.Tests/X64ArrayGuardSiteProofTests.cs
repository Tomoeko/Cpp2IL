using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;
using Classification = Cpp2IL.Core.InstructionSets.X64UnwindProof.SpanClassification;
using SpanKind = Cpp2IL.Core.InstructionSets.X64UnwindProof.SpanKind;

namespace Cpp2IL.Core.Tests;

/// <summary>
/// The optional player gate reads a locally built neutral fixture. No player
/// bytes or generated native code are stored in the public test assembly.
/// </summary>
[NonParallelizable]
public class X64ArrayGuardSiteProofTests
{
    [Test]
    public void UnwindClosedPrefixExcludesOverscannedNativeTail()
    {
        const ulong entry = 0x1000;
        var original = SyntheticBody(entry);
        var end = original[^1].NextIP;
        var tail = X86Utils.Iterate(new byte[] { 0xCC, 0xCC, 0xC3 }.AsSpan(),
            end, false).ToArray();
        var overscanned = original.Concat(tail).ToArray();
        Classification FirstRegion(ulong _, ulong requestedEnd) =>
            requestedEnd <= end
                ? new Classification(SpanKind.HandlerFree, entry, end, entry)
                : new Classification(SpanKind.Unsupported, entry, end);

        var selected = X64ArrayGuardSiteProof.SelectUnwindClosedPrefix(
            overscanned, entry, FirstRegion);
        Assert.That(selected, Is.Not.Null);
        Assert.That(selected, Has.Count.EqualTo(original.Length));
        Assert.That(selected![^1].NextIP, Is.EqualTo(end));
        Assert.That(Native(selected, entry, FirstRegion), Is.Not.Null);

        var malformed = overscanned.ToArray();
        malformed[original.Length - 2].Code = Code.Jmp_rel8_64;
        Assert.That(X64ArrayGuardSiteProof.SelectUnwindClosedPrefix(
            malformed, entry, FirstRegion), Is.Null);
        Assert.That(X64ArrayGuardSiteProof.SelectUnwindClosedPrefix(
            overscanned, entry,
            (_, _) => new Classification(SpanKind.HandlerFree,
                entry, end + 1, entry)), Is.Null);
        Assert.That(X64ArrayGuardSiteProof.SelectUnwindClosedPrefix(
            overscanned, entry,
            (_, _) => new Classification(SpanKind.Unsupported,
                entry, end)), Is.Null);

        var escaping = selected.ToArray();
        escaping[0].Code = Code.Jmp_rel8_64;
        escaping[0].NearBranch64 = tail[0].IP;
        Assert.That(Native(escaping, entry, FirstRegion), Is.Null,
            "a reachable edge may not escape the proved prefix");
    }

    [Test]
    public void SyntheticTwoSiteGraphRequiresEachGuardAndEffectBoundary()
    {
        const ulong entry = 0x1000;
        var body = SyntheticBody(entry);
        var classify = CompleteRegion(body);
        var proof = Native(body, entry, classify);
        Assert.That(proof, Is.Not.Null);
        Assert.That(proof!.Sites, Has.Count.EqualTo(2));
        Assert.That(proof.Comparison, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(proof.Sites[0].Kind,
                Is.EqualTo(X64ArrayGuardSiteProof.ReadKind.Move));
            Assert.That(proof.Sites[1].Kind,
                Is.EqualTo(X64ArrayGuardSiteProof.ReadKind.Compare));
            Assert.That(proof.Sites[1].EffectsSincePreviousAccess,
                Has.Count.EqualTo(2));
            Assert.That(proof.Sites[1].EffectsSincePreviousAccess[0].Kind,
                Is.EqualTo(X64ArrayGuardSiteProof.EffectKind.MemoryStore));
            Assert.That(proof.Sites[1].EffectsSincePreviousAccess[0].StoreWidth,
                Is.EqualTo(4));
            Assert.That(proof.Sites[1].EffectsSincePreviousAccess[1].Kind,
                Is.EqualTo(X64ArrayGuardSiteProof.EffectKind.DirectCall));
            Assert.That(proof.Sites[1].EffectsSincePreviousAccess[1].DirectCallTarget,
                Is.Not.Zero);
            Assert.That(proof.Sites[0].IndexArgument, Is.EqualTo(Register.RDX));
            Assert.That(proof.Sites[1].IndexArgument, Is.EqualTo(Register.R8));
            Assert.That(proof.Comparison!.LeftElementRead,
                Is.EqualTo(proof.Sites[0].ElementRead));
            Assert.That(proof.Comparison.RightElementRead,
                Is.EqualTo(proof.Sites[1].ElementRead));
            Assert.That(proof.Comparison.LeftRegister, Is.EqualTo(Register.RSI));
        });

        Reject("second array reload uses another owner", changed =>
            changed[proof.Sites[1].ArrayFieldRead].MemoryBase = Register.RSI);
        Reject("second index is extended from another argument", changed =>
        {
            var extension = Array.FindIndex(changed,
                instruction => instruction.Code == Code.Movsxd_r64_rm32 &&
                               instruction.Op0Register == Register.RDI);
            changed[extension].Op1Register = Register.R10D;
        });
        Reject("second element address uses a different index", changed =>
            changed[proof.Sites[1].ElementRead].MemoryIndex = Register.RAX);
        Reject("comparison left register is not the first element", changed =>
            changed[proof.Sites[1].ElementRead].Op0Register = Register.RAX);
        Reject("SETE writes another byte", changed =>
            changed[proof.Comparison!.SetEqual].Op0Register = Register.CL);
        Reject("extended index has another observable use", changed =>
        {
            var otherUse = proof.Sites[1].ElementRead + 1;
            changed[otherUse].Code = Code.Mov_r64_rm64;
            changed[otherUse].Op0Kind = OpKind.Register;
            changed[otherUse].Op0Register = Register.RAX;
            changed[otherUse].Op1Kind = OpKind.Register;
            changed[otherUse].Op1Register = Register.RDI;
        });
        Reject("an edge bypasses the first guard", changed =>
        {
            changed[0].Code = Code.Jmp_rel8_64;
            changed[0].NearBranch64 = changed[proof.Sites[0].ElementRead].IP;
        });
        Reject("an extra edge enters a guard setup", changed =>
        {
            changed[0].Code = Code.Jne_rel8_64;
            changed[0].NearBranch64 = changed[proof.Sites[1].ArrayNullBranch - 1].IP;
        });
        Reject("an effect appears after a bounds check", changed =>
            changed[proof.Sites[0].BoundsBranch + 1].Code = Code.Inc_rm32);
        Reject("a helper has another predecessor", changed =>
        {
            changed[0].Code = Code.Jne_rel8_64;
            changed[0].NearBranch64 = changed[proof.NullHelperCall].IP;
        });
        Assert.That(Native(body, entry,
            (_, _) => new Classification(SpanKind.Unsupported, 0, 0)), Is.Null);
        Assert.That(X64ArrayGuardSiteProof.TryProveNative(body, entry,
            classify, _ => false, _ => true), Is.Null);
        Assert.That(X64ArrayGuardSiteProof.TryProveNative(body, entry,
            classify, _ => true, _ => false), Is.Null);
        var earlyEffect = SyntheticBody(entry, effectBeforeFirstField: true);
        Assert.That(Native(earlyEffect, entry, CompleteRegion(earlyEffect)), Is.Null,
            "an effect before the owner's first bound field read is not movable");
        var referenceClobber = SyntheticBody(entry, clobberComparedReference: true);
        Assert.That(Native(referenceClobber, entry,
            CompleteRegion(referenceClobber)), Is.Null,
            "the first element reference must survive every intervening call");
        var extraFlagConsumer = SyntheticBody(entry, extraFlagConsumer: true);
        Assert.That(Native(extraFlagConsumer, entry,
            CompleteRegion(extraFlagConsumer)), Is.Null,
            "SETE AL must be the only live comparison-flag consumer");
        var boundsFlagConsumer = SyntheticBody(entry, extraBoundsFlagConsumer: true);
        Assert.That(Native(boundsFlagConsumer, entry,
            CompleteRegion(boundsFlagConsumer)), Is.Null,
            "a suppressed bounds CMP cannot feed SETAE after the element read");
        var trailingEffect = SyntheticBody(entry, effectAfterComparison: true);
        Assert.That(Native(trailingEffect, entry,
            CompleteRegion(trailingEffect)), Is.Null,
            "a store after the final read needs its own effect-order proof");

        void Reject(string reason, Action<Instruction[]> change)
        {
            var changed = body.ToArray();
            change(changed);
            Assert.That(Native(changed, entry, classify), Is.Null, reason);
        }
    }

    [Test]
    public void ExactOriginalMethodsHaveSeparateGuardedReadsAndEffectBarriers()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_COMPOSED_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_COMPOSED_ARRAY_FIXTURE_INPUT to the fixture player-input directory.");
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
            var methods = app.GetAssemblyByName("ComposedArrayFixture")!.Types
                .SelectMany(type => type.Methods).ToArray();
            Assert.That(methods, Has.Length.EqualTo(6));
            foreach (var name in new[] { "ReadTwice", "ReadAcrossReplacement" })
            {
                var method = methods.Single(candidate => candidate.Name == name);
                var native = X86Utils.Iterate(method).ToArray();
                var proof = X64ArrayGuardSiteProof.Find(method, native);
                Assert.That(proof, Is.Not.Null, name);
                Assert.That(proof!.Sites, Has.Count.EqualTo(2), name);
                Assert.That(proof.Comparison, Is.Not.Null, name);
                Assert.Multiple(() =>
                {
                    Assert.That(proof.Sites[0].Kind,
                        Is.EqualTo(X64ArrayGuardSiteProof.ReadKind.Move));
                    Assert.That(proof.Sites[1].Kind,
                        Is.EqualTo(X64ArrayGuardSiteProof.ReadKind.Compare));
                    Assert.That(proof.Sites[0].ArrayFieldReadIp,
                        Is.LessThan(proof.Sites[0].ElementReadIp));
                    Assert.That(proof.Sites[0].ElementReadIp,
                        Is.LessThan(proof.Sites[1].ArrayFieldReadIp));
                    Assert.That(proof.Sites[1].EffectsSincePreviousAccess,
                        Has.Count.EqualTo(2));
                    Assert.That(proof.Sites[1].EffectsSincePreviousAccess[0].Kind,
                        Is.EqualTo(X64ArrayGuardSiteProof.EffectKind.MemoryStore));
                    Assert.That(proof.Sites[1].EffectsSincePreviousAccess[0].StoreWidth,
                        Is.EqualTo(4));
                    Assert.That(proof.Sites[1].EffectsSincePreviousAccess[1].Kind,
                        Is.EqualTo(X64ArrayGuardSiteProof.EffectKind.DirectCall));
                    Assert.That(proof.Sites[1].EffectsSincePreviousAccess[1].DirectCallTarget,
                        Is.Not.Zero);
                    Assert.That(proof.Comparison!.LeftElementReadIp,
                        Is.EqualTo(proof.Sites[0].ElementReadIp));
                    Assert.That(proof.Comparison.RightElementReadIp,
                        Is.EqualTo(proof.Sites[1].ElementReadIp));
                    Assert.That(proof.Comparison.CompareIp,
                        Is.EqualTo(proof.Sites[1].ElementReadIp));
                    Assert.That(proof.Comparison.SetEqualIp,
                        Is.GreaterThan(proof.Comparison.CompareIp));
                    Assert.That(proof.Comparison.LeftRegister, Is.EqualTo(Register.RSI));
                    Assert.That(proof.NativeEndExclusiveIp,
                        Is.GreaterThan(proof.BoundsTrapIp));
                    Assert.That(proof.Sites[0].OwnerFieldReadIp, Is.Not.Null);
                    Assert.That(proof.Sites[1].OwnerFieldReadIp,
                        Is.EqualTo(proof.Sites[0].OwnerFieldReadIp));
                    Assert.That(proof.Sites[0].OwnerField,
                        Is.SameAs(proof.Sites[1].OwnerField));
                    Assert.That(proof.Sites[0].OwnerNullTestIp,
                        Is.LessThan(proof.Sites[0].OwnerNullBranchIp));
                    Assert.That(proof.Sites[1].ArrayNullTestIp,
                        Is.LessThan(proof.Sites[1].ArrayNullBranchIp));
                    Assert.That(proof.Sites[1].BoundsCompareIp,
                        Is.LessThan(proof.Sites[1].BoundsBranchIp));
                    Assert.That(proof.Sites[0].IndexExtensionIp,
                        Is.LessThan(proof.Sites[0].ElementReadIp));
                    Assert.That(proof.Sites[1].IndexExtensionIp,
                        Is.LessThan(proof.Sites[1].ElementReadIp));
                    Assert.That(proof.Sites[0].IndexArgument, Is.EqualTo(Register.RDX));
                    Assert.That(proof.Sites[1].IndexArgument, Is.EqualTo(Register.R8));
                });

                var complete = native.Append(Decoder.Create(64,
                    new ByteArrayCodeReader([0xCC]), native[^1].NextIP).Decode()).ToArray();
                var unwind = X64UnwindProof.ForApplication(app)!;
                var original = Native(complete, method.UnderlyingPointer,
                    unwind.ClassifySpan);
                Assert.That(original, Is.Not.Null);

                Reject("second array reload changed", changed =>
                    changed[original!.Sites[1].ArrayFieldRead].MemoryBase = Register.RSI);
                Reject("second index extension changed", changed =>
                    changed[5].Code = Code.Mov_r64_rm64);
                Reject("second element uses another index", changed =>
                    changed[original!.Sites[1].ElementRead].MemoryIndex = Register.RAX);
                Reject("comparison left value has another register", changed =>
                    changed[original!.Sites[1].ElementRead].Op0Register = Register.RAX);
                Reject("SETE result has another register", changed =>
                    changed[original!.Comparison!.SetEqual].Op0Register = Register.CL);
                Reject("guard no longer dominates read", changed =>
                {
                    changed[0].Code = Code.Jmp_rel8_64;
                    changed[0].NearBranch64 = changed[original!.Sites[1].ElementRead].IP;
                });
                Reject("effect inserted between bounds and read", changed =>
                    changed[original!.Sites[0].BoundsBranch + 1].Code = Code.Inc_rm32);
                Reject("extra null-helper predecessor", changed =>
                {
                    changed[0].Code = Code.Jne_rel8_64;
                    changed[0].NearBranch64 = changed[original!.NullHelperCall].IP;
                });

                Assert.That(Native(complete, method.UnderlyingPointer,
                    (_, _) => new Classification(SpanKind.Unsupported, 0, 0)), Is.Null);
                Assert.That(X64ArrayGuardSiteProof.TryProveNative(complete,
                    method.UnderlyingPointer, unwind.ClassifySpan,
                    _ => false, _ => true), Is.Null);

                var alteredRaw = native.ToArray();
                alteredRaw[original.Sites[1].ArrayFieldRead].MemoryDisplacement64 += 8;
                Assert.That(X64ArrayGuardSiteProof.Find(method, alteredRaw), Is.Null,
                    "a native mutation cannot pass the file-backed production gate");

                void Reject(string reason, Action<Instruction[]> change)
                {
                    var mutated = complete.ToArray();
                    change(mutated);
                    Assert.That(Native(mutated, method.UnderlyingPointer,
                        unwind.ClassifySpan), Is.Null, name + ": " + reason);
                }
            }
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }

    [Test]
    public void ExactOriginalOverscannedMethodEndsAtUnwindBoundary()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_COMPOSED_READ_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_COMPOSED_READ_FIXTURE_INPUT to the fixture player-input directory.");
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
            var method = app.GetAssemblyByName("ComposedReadFixture")!.Types
                .SelectMany(type => type.Methods)
                .Single(candidate => candidate.Name == "ReadTwice");
            var decoded = X86Utils.Iterate(method).ToArray();
            var proof = X64ArrayGuardSiteProof.Find(method, decoded);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.Sites, Has.Count.EqualTo(2));
            Assert.That(proof.Comparison, Is.Not.Null);
            Assert.That(proof.NativeEndExclusiveIp,
                Is.LessThan(decoded[^1].NextIP));
            var prefix = decoded.TakeWhile(instruction =>
                instruction.IP < proof.NativeEndExclusiveIp).ToArray();
            Assert.That(prefix[^1].NextIP,
                Is.EqualTo(proof.NativeEndExclusiveIp));
            Assert.That(prefix[^1].Code, Is.EqualTo(Code.Int3));

            var altered = decoded.ToArray();
            altered[prefix.Length - 2].Code = Code.Jmp_rel8_64;
            Assert.That(X64ArrayGuardSiteProof.Find(method, altered), Is.Null,
                "file-backed decoding cannot admit a changed terminal arm");
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }

    [TestCase("second-array-alias")]
    [TestCase("removed-counter")]
    [TestCase("removed-call")]
    [TestCase("wrong-call-target")]
    [TestCase("ambiguous-call-binding")]
    [TestCase("wrong-call-receiver")]
    [TestCase("wrong-call-argument")]
    public void FinalManagedAccessMustRetainProvedArrayAndEffects(string mutation)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_COMPOSED_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_COMPOSED_ARRAY_FIXTURE_INPUT to the fixture player-input directory.");
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
            var method = app.GetAssemblyByName("ComposedArrayFixture")!.Types
                .SelectMany(type => type.Methods)
                .Single(candidate => candidate.Name == "ReadAcrossReplacement");
            method.Analyze();
            var evidence = method.GuardedArrayAccessEvidence;
            Assert.That(evidence, Is.Not.Null);
            Assert.DoesNotThrow(() => IlGenerator.ValidateGuardedArrayAccesses(method),
                "the unmodified synthetic fixture must pass the final managed-access gate");

            var graph = method.ControlFlowGraph!;
            var firstField = graph.Instructions.Single(instruction =>
                instruction.NativeAddress == evidence!.Sites[0].ArrayFieldReadIp &&
                instruction is { OpCode: Cpp2IL.Core.ISIL.OpCode.Move,
                    Operands: [Cpp2IL.Core.ISIL.LocalVariable, Cpp2IL.Core.ISIL.FieldReference] });
            var secondElement = graph.Instructions.Single(instruction =>
                instruction.NativeAddress == evidence!.Sites[1].ElementReadIp &&
                instruction is { OpCode: Cpp2IL.Core.ISIL.OpCode.Move,
                    Operands: [Cpp2IL.Core.ISIL.LocalVariable, Cpp2IL.Core.ISIL.ArrayAccess] });
            var effectIp = evidence!.Sites[1].EffectsSincePreviousAccess[
                mutation is "removed-call" or "wrong-call-target" or
                    "ambiguous-call-binding" or "wrong-call-receiver" or
                    "wrong-call-argument" ? 1 : 0].Ip;
            var effect = graph.Instructions.Single(instruction =>
                instruction.NativeAddress == effectIp && instruction.OpCode is
                    Cpp2IL.Core.ISIL.OpCode.Add or Cpp2IL.Core.ISIL.OpCode.CallVoid);
            switch (mutation)
            {
                case "second-array-alias":
                    ((Cpp2IL.Core.ISIL.ArrayAccess)secondElement.Operands[1]).Array =
                        (Cpp2IL.Core.ISIL.LocalVariable)firstField.Operands[0];
                    break;
                case "removed-counter":
                case "removed-call":
                    graph.FindBlockByInstruction(effect)!.Instructions.Remove(effect);
                    break;
                case "wrong-call-target":
                    effect.SetOperand(0, app.GetAssemblyByName("ComposedArrayFixture")!.Types
                        .SelectMany(type => type.Methods)
                        .Single(candidate => candidate.Name == "Mark"));
                    break;
                case "ambiguous-call-binding":
                    var callTarget = (MethodAnalysisContext)effect.Operands[0];
                    app.MethodsByAddress[callTarget.UnderlyingPointer].Add(method);
                    break;
                case "wrong-call-receiver":
                    var receiver = (Cpp2IL.Core.ISIL.LocalVariable)effect.Operands[1];
                    effect.SetOperand(1, new Cpp2IL.Core.ISIL.LocalVariable("otherReceiver",
                        receiver.Register.Copy(2), receiver.Type));
                    break;
                case "wrong-call-argument":
                    var argument = (Cpp2IL.Core.ISIL.LocalVariable)effect.Operands[2];
                    effect.SetOperand(2, new Cpp2IL.Core.ISIL.LocalVariable("otherArgument",
                        argument.Register.Copy(2), argument.Type));
                    break;
            }

            Assert.That(() => IlGenerator.ValidateGuardedArrayAccesses(method),
                Throws.TypeOf<DecompilerException>().With.Message.Contains("Guarded array access proof"),
                mutation);
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }

    private static X64ArrayGuardSiteProof.NativeEvidence? Native(
        IReadOnlyList<Instruction> body, ulong entry,
        Func<ulong, ulong, Classification> classify) =>
        X64ArrayGuardSiteProof.TryProveNative(body, entry, classify,
            _ => true, _ => true);

    private static Func<ulong, ulong, Classification> CompleteRegion(
        IReadOnlyList<Instruction> body) =>
        (_, _) => new Classification(SpanKind.HandlerFree,
            body[0].IP, body[^1].NextIP, body[0].IP);

    private static Instruction[] SyntheticBody(ulong entry,
        bool effectBeforeFirstField = false,
        bool clobberComparedReference = false,
        bool extraFlagConsumer = false,
        bool extraBoundsFlagConsumer = false,
        bool effectAfterComparison = false)
    {
        var assembler = new Assembler(64);
        var nullExit = assembler.CreateLabel();
        var boundsExit = assembler.CreateLabel();
        assembler.mov(rbx, rcx);
        assembler.test(rbx, rbx);
        assembler.je(nullExit);
        if (effectBeforeFirstField)
            assembler.inc(__dword_ptr[rbx + 0x2C]);
        else
            assembler.mov(r10, r10);
        assembler.movsxd(rdi, r8d);
        assembler.mov(r9, __qword_ptr[rbx + 0x18]);
        assembler.test(r9, r9);
        assembler.je(nullExit);
        assembler.cmp(edx, __dword_ptr[r9 + 0x18]);
        assembler.jae(boundsExit);
        assembler.movsxd(rax, edx);
        assembler.mov(rsi, __qword_ptr[r9 + rax * 8 + 0x20]);
        if (extraBoundsFlagConsumer)
            assembler.setae(al);
        assembler.inc(__dword_ptr[rbx + 0x28]);
        assembler.call(0x4000UL);
        if (clobberComparedReference)
            assembler.mov(rsi, rax);
        assembler.mov(rcx, __qword_ptr[rbx + 0x18]);
        assembler.test(rcx, rcx);
        assembler.je(nullExit);
        assembler.cmp(edi, __dword_ptr[rcx + 0x18]);
        assembler.jae(boundsExit);
        assembler.cmp(rsi, __qword_ptr[rcx + rdi * 8 + 0x20]);
        if (extraFlagConsumer)
            assembler.sete(cl);
        assembler.sete(al);
        if (effectAfterComparison)
            assembler.inc(__dword_ptr[rbx + 0x28]);
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
