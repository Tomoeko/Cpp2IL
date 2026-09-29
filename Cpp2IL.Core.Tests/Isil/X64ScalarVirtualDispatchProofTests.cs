using System;
using System.IO;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ScalarVirtualDispatchProofTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void InvocationKeepsReceiverInputPairedSlotAndOrderedStore(bool store)
    {
        var body = Caller(store);
        var proof = X64ScalarVirtualDispatchProof.TryProveShape(body);
        Assert.That(proof, Is.Not.Null);
        Assert.That(proof!.SlotOffset, Is.EqualTo(0x198UL));
        Assert.That(proof.IsTail, Is.EqualTo(!store));
        Assert.That(proof.Store.HasValue, Is.EqualTo(store));
        Assert.That(proof.NullCall.NearBranchTarget, Is.EqualTo(0x4000));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RegisterMemoryAndControlFlowSubstitutionsCannotChangeTheInvocation(bool store)
    {
        foreach (var mutation in new[] { "stack", "restore", "receiver-test", "test-width", "condition", "failure-edge",
                     "klass-receiver", "klass-width", "klass-index", "klass-destination", "method-pointer", "method-width",
                     "method-info-base", "method-info-pair", "method-info-destination", "invoke", "invoke-register",
                     "null-helper-indirect", "null-helper-return", "trap-effect", "gap", "lock", "rep", "segment", "code32",
                     "extra-operation", "missing-trap" })
        {
            var body = Caller(store);
            var first = store ? 3 : 1;
            var invoke = store ? 8 : 7;
            switch (mutation)
            {
                case "stack": body[store ? 1 : 0].Immediate8 = 0x18; break;
                case "restore": body[store ? 12 : 6].Immediate8 = 0x18; break;
                case "receiver-test": body[first].Op1Register = Register.RDX; break;
                case "test-width": body[first].Code = Code.Test_rm32_r32; break;
                case "condition": body[first + 1].Code = Code.Jne_rel8_64; break;
                case "failure-edge": body[first + 1].NearBranch64 = body[invoke].IP; break;
                case "klass-receiver": body[first + 2].MemoryBase = Register.RDX; break;
                case "klass-width": body[first + 2].Code = Code.Mov_r32_rm32; break;
                case "klass-index": body[first + 2].MemoryIndex = Register.RDX; break;
                case "klass-destination": body[first + 2].Op0Register = Register.R9; break;
                case "method-pointer": body[first + 3].MemoryBase = Register.R9; break;
                case "method-width": body[first + 3].Code = Code.Mov_r32_rm32; break;
                case "method-info-base": body[first + 4].MemoryBase = Register.R9; break;
                case "method-info-pair": body[first + 4].MemoryDisplacement64 += 8; break;
                case "method-info-destination": body[first + 4].Op0Register = Register.R9; break;
                case "invoke": body[invoke].Code = store ? Code.Jmp_rm64 : Code.Call_rm64; break;
                case "invoke-register": body[invoke].Op0Register = Register.RDX; break;
                case "null-helper-indirect": body[^2].Code = Code.Call_rm64; break;
                case "null-helper-return": body[^2].Code = Code.Retnq; break;
                case "trap-effect": body[^1].Code = Code.Inc_rm32; break;
                case "gap": body[invoke].IP++; break;
                case "lock": body[invoke].HasLockPrefix = true; break;
                case "rep": body[invoke].HasRepPrefix = true; break;
                case "segment": body[first + 2].SegmentPrefix = Register.FS; break;
                case "code32": body[invoke].CodeSize = CodeSize.Code32; break;
                case "extra-operation": body = body.Append(body[^1]).ToArray(); break;
                case "missing-trap": body = body[..^1]; break;
            }
            Assert.That(X64ScalarVirtualDispatchProof.TryProveShape(body), Is.Null, mutation);
        }
    }

    [TestCase("save")]
    [TestCase("capture")]
    [TestCase("sink-test")]
    [TestCase("sink-edge")]
    [TestCase("sink-base")]
    [TestCase("sink-index")]
    [TestCase("sink-width")]
    [TestCase("sink-header")]
    [TestCase("stored-result")]
    [TestCase("restore-register")]
    [TestCase("return-pop")]
    public void PostCallStoreCannotMoveChangeWidthOrReuseAnotherReceiver(string mutation)
    {
        var body = Caller(true);
        switch (mutation)
        {
            case "save": body[0].Op0Register = Register.RSI; break;
            case "capture": body[2].Op1Register = Register.R9; break;
            case "sink-test": body[9].Op1Register = Register.RCX; break;
            case "sink-edge": body[10].NearBranch64 = body[14].IP; break;
            case "sink-base": body[11].MemoryBase = Register.RCX; break;
            case "sink-index": body[11].MemoryIndex = Register.RCX; break;
            case "sink-width": body[11].Code = Code.Mov_rm64_r64; break;
            case "sink-header": body[11].MemoryDisplacement64 = 8; break;
            case "stored-result": body[11].Op1Register = Register.EDX; break;
            case "restore-register": body[13].Op0Register = Register.RSI; break;
            case "return-pop": body[14].Code = Code.Retnq_imm16; break;
        }
        Assert.That(X64ScalarVirtualDispatchProof.TryProveShape(body), Is.Null);
    }

    [Test]
    public void MissingPlayerMetadataCannotProveADispatch() => Assert.That(X64ScalarVirtualDispatchProof.Find(null), Is.Null);

    [Test]
    public void VirtualSemanticsCannotBeAttachedToAReadOrDroppedByAnOpcodeChange()
    {
        var read = new Cpp2IL.Core.ISIL.Instruction(0, Cpp2IL.Core.ISIL.OpCode.Move);
        Assert.Throws<InvalidOperationException>(() => read.CallSemantics = Cpp2IL.Core.ISIL.CallSemantics.VirtualDispatch);
        var call = new Cpp2IL.Core.ISIL.Instruction(0, Cpp2IL.Core.ISIL.OpCode.Call)
            { CallSemantics = Cpp2IL.Core.ISIL.CallSemantics.VirtualDispatch };
        Assert.Throws<InvalidOperationException>(() => call.OpCode = Cpp2IL.Core.ISIL.OpCode.Move);
        Assert.That(call.CallSemantics, Is.EqualTo(Cpp2IL.Core.ISIL.CallSemantics.VirtualDispatch));
    }

    private static Instruction[] Caller(bool store)
    {
        var assembler = new Assembler(64);
        var failure = assembler.CreateLabel();
        if (store)
        {
            assembler.push(rbx);
            assembler.sub(rsp, 0x20);
            assembler.mov(rbx, r8);
        }
        else assembler.sub(rsp, 0x28);
        assembler.test(rcx, rcx);
        assembler.je(failure);
        assembler.mov(r8, __qword_ptr[rcx]);
        assembler.mov(rax, __qword_ptr[r8 + 0x198]);
        assembler.mov(r8, __qword_ptr[r8 + 0x1a0]);
        if (store)
        {
            assembler.call(rax);
            assembler.test(rbx, rbx);
            assembler.je(failure);
            assembler.mov(__dword_ptr[rbx + 24], eax);
            assembler.add(rsp, 0x20);
            assembler.pop(rbx);
            assembler.ret();
        }
        else
        {
            assembler.add(rsp, 0x28);
            assembler.jmp(rax);
        }
        assembler.Label(ref failure);
        assembler.call(0x4000);
        assembler.int3();
        using var bytes = new MemoryStream();
        assembler.Assemble(new StreamCodeWriter(bytes), 0x1000);
        return X86Utils.Iterate(bytes.ToArray(), 0x1000, false).ToArray();
    }
}
