using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Iced.Intel;
using NativeInstruction = Iced.Intel.Instruction;
using IsilInstruction = Cpp2IL.Core.ISIL.Instruction;
using IsilRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.Tests.Isil;

public class X86IntegerExtensionProofTests
{
    [TestCase("0FBEC1C3", 8, 32, 0x80UL, 0xFFFFFF80UL)] // movsx eax,cl
    [TestCase("0FBEC1C3", 8, 64, 0x80UL, 0xFFFFFF80UL)] // EAX write zeros upper RAX
    [TestCase("0FB6C1C3", 8, 32, 0xFFUL, 0xFFUL)] // movzx eax,cl
    [TestCase("0FB6C1C3", 8, 64, 0xFFUL, 0xFFUL)]
    [TestCase("0FBFC1C3", 16, 32, 0x8000UL, 0xFFFF8000UL)] // movsx eax,cx
    [TestCase("0FBFC1C3", 16, 64, 0x8000UL, 0xFFFF8000UL)]
    [TestCase("0FB7C1C3", 16, 64, 0xFFFFUL, 0xFFFFUL)] // movzx eax,cx
    [TestCase("4863C1C3", 32, 64, 0x80000000UL, 0xFFFFFFFF80000000UL)] // movsxd rax,ecx
    [TestCase("8BC1C3", 32, 64, 0x80000000UL, 0x80000000UL)] // mov eax,ecx
    [TestCase("480FBEC1C3", 8, 64, 0x80UL, 0xFFFFFFFFFFFFFF80UL)] // movsx rax,cl
    [TestCase("480FBFC1C3", 16, 64, 0x8000UL, 0xFFFFFFFFFFFF8000UL)]
    [TestCase("480FB6C1C3", 8, 64, 0xFFUL, 0xFFUL)] // movzx rax,cl
    [TestCase("480FB7C1C3", 16, 64, 0xFFFFUL, 0xFFFFUL)]
    [TestCase("0FB6C1480FBEC0C3", 8, 64, 0x80UL, 0xFFFFFFFFFFFFFF80UL)] // defined AL
    [TestCase("0FB7C1480FBFC0C3", 16, 64, 0x8000UL, 0xFFFFFFFFFFFF8000UL)] // defined AX
    [TestCase("8BC14863C0C3", 32, 64, 0x80000000UL, 0xFFFFFFFF80000000UL)] // defined EAX
    [TestCase("0FBEC1480FB7C0C3", 8, 64, 0x80UL, 0xFF80UL)] // conversion reads only AX
    [TestCase("0FB7C198C3", 16, 64, 0x8000UL, 0xFFFF8000UL)] // CWDE preserves the EAX zeroing rule
    [TestCase("0FBEC14898C3", 8, 64, 0x80UL, 0xFFFFFFFFFFFFFF80UL)] // CDQE uses defined EAX
    [TestCase("480FBEC10FB6C0C3", 8, 64, 0x80UL, 0x80UL)] // later EAX write discards old upper RAX
    public void ProvesNativeRegisterWidthsAndFullAccumulatorState(string bytes, int parameterBits, int returnBits, ulong input, ulong expected)
    {
        var lifted = X86IntegerExtensionProof.TryLift(Decode(bytes), parameterBits, returnBits);
        Assert.That(lifted, Is.Not.Null);
        Assert.That(Evaluate(lifted!, input), Is.EqualTo(expected));
        // Arbitrary upper argument bits must not affect any proved low-bit conversion.
        var unspecifiedBits = ~((1UL << parameterBits) - 1);
        Assert.That(Evaluate(lifted!, input | unspecifiedBits), Is.EqualTo(expected));
    }

    [TestCase("0FBEC1C3", 8, 32)]
    [TestCase("0FBFC1C3", 16, 64)]
    [TestCase("4863C1C3", 32, 64)]
    public void PreservesZeroAndPositiveBoundaryValues(string bytes, int parameterBits, int returnBits)
    {
        var lifted = X86IntegerExtensionProof.TryLift(Decode(bytes), parameterBits, returnBits)!;
        foreach (var value in new[] { 0UL, 1UL, (1UL << (parameterBits - 1)) - 1 })
            Assert.That(Evaluate(lifted, value), Is.EqualTo(value));
    }

    [TestCase("0FBFC1C3", 8, 32)] // CX contains bits outside the parameter
    [TestCase("4863C1C3", 8, 64)] // narrow signed parameter does not prove sign-filled ECX
    [TestCase("4863C1C3", 16, 64)]
    [TestCase("8BC1C3", 8, 64)]
    [TestCase("8BC1C3", 16, 64)]
    [TestCase("0FBEC0C3", 8, 32)] // AL is undefined at entry
    [TestCase("0FBFC0C3", 16, 32)]
    [TestCase("4863C0C3", 32, 64)]
    [TestCase("98C3", 16, 32)]
    [TestCase("4898C3", 32, 64)]
    [TestCase("0FBEC2C3", 8, 32)] // DL is not the argument
    [TestCase("0FBEC4C3", 8, 32)] // AH is not a low argument lane
    [TestCase("0FBE01C3", 8, 32)] // memory load
    [TestCase("0FB6C18901C3", 8, 32)] // memory store after the conversion
    [TestCase("660FBEC1C3", 8, 32)] // AX destination leaves upper bits untouched
    [TestCase("8AC1C3", 8, 32)] // AL partial write
    [TestCase("488BC1C3", 32, 64)] // RCX high bits are not established
    [TestCase("8BC1C3", 32, 32)] // plain copy is outside the conversion proof
    [TestCase("0FB6C1B080C3", 8, 64)] // partial overwrite after a known value
    [TestCase("0FB6C131C0C3", 8, 64)] // arithmetic is outside this proof
    [TestCase("0FB6C1E800000000C3", 8, 64)] // opaque call
    [TestCase("EB000FB6C1C3", 8, 64)] // branch, even if it reaches the conversion
    [TestCase("0FB6C1EB00C3", 8, 64)]
    [TestCase("74040FB6C1C3C3", 8, 64)] // alternate return bypass
    [TestCase("0FB6C15058C3", 8, 64)] // stack effects
    [TestCase("0FB6C190C3", 8, 64)] // unlisted instruction
    [TestCase("0FB6C1", 8, 64)] // no real return
    [TestCase("0FB6C1C20000", 8, 64)] // RET imm16 is not the plain ABI return
    [TestCase("0FB6C1F3C3", 8, 64)] // unsupported prefix
    [TestCase("640FB6C1C3", 8, 64)]
    [TestCase("C3", 8, 64)] // no value definition
    [TestCase("0FB6C1C3", 64, 64)] // input signature outside bounded scope
    [TestCase("0FB6C1C3", 8, 16)]
    [TestCase("0FB6C1C3", 0, 32)]
    public void RejectsUnknownBitsEffectsOrNoncanonicalBody(string bytes, int parameterBits, int returnBits)
    {
        Assert.That(X86IntegerExtensionProof.TryLift(Decode(bytes), parameterBits, returnBits), Is.Null);
    }

    [Test]
    public void KeepsDistinctAccumulatorValuesAndFinalReturnProjection()
    {
        var lifted = X86IntegerExtensionProof.TryLift(Decode("0FBEC1C3"), 8, 32)!;
        Assert.That(lifted.Select(i => i.OpCode), Is.EqualTo(new[] { OpCode.IntegerExtend, OpCode.IntegerExtend, OpCode.IntegerExtend, OpCode.Return }));
        var destinations = lifted.Take(3).Select(i => (IsilRegister)i.Operands[0]).ToArray();
        Assert.That(destinations.Distinct().Count(), Is.EqualTo(3));
        Assert.That(lifted[0].Operands[1], Is.EqualTo(new IsilRegister(null, "rcx")));
        Assert.That(lifted[1].Operands[1], Is.EqualTo(destinations[0]));
        Assert.That(lifted[1].Operands.Skip(2).Cast<Immediate>().Select(i => i.Value), Is.EqualTo(new long[] { 32, 64, 0 }));
        Assert.That(lifted[2].Operands[1], Is.EqualTo(destinations[1]));
        Assert.That(lifted[2].Operands.Skip(2).Cast<Immediate>().Select(i => i.Value), Is.EqualTo(new long[] { 32, 32, 0 }));
        Assert.That(lifted[3].Operands[0], Is.EqualTo(destinations[2]));
    }

    [Test]
    public void ClosedReturnMayIgnoreUnreachablePaddingAndAdjacentBody()
    {
        var body = Decode("0FB6C1C3CCFFE1488B0140B601C3");
        var lifted = X86IntegerExtensionProof.TryLift(body, 8, 64);
        Assert.That(lifted, Is.Not.Null);
        Assert.That(Evaluate(lifted!, 0xFF), Is.EqualTo(0xFF));
    }

    [Test]
    public void RejectsMissingEntryBytesAndNon64BitDecoding()
    {
        var body = Decode("0FB6C1C3");
        var terminator = body[1];
        terminator.IP++;
        body[1] = terminator;
        Assert.That(X86IntegerExtensionProof.TryLift(body, 8, 64), Is.Null);
        Assert.That(X86IntegerExtensionProof.TryLift(Decode("0FB6C1C3", 32), 8, 64), Is.Null);
    }

    private static ulong Evaluate(IEnumerable<IsilInstruction> body, ulong argument)
    {
        var values = new Dictionary<IsilRegister, ulong> { [new(null, "rcx")] = argument };
        foreach (var instruction in body)
        {
            if (instruction.OpCode == OpCode.Return)
                return values[(IsilRegister)instruction.Operands[0]];
            Assert.That(instruction.OpCode, Is.EqualTo(OpCode.IntegerExtend));
            var sourceBits = (int)((Immediate)instruction.Operands[2]).Value;
            var resultBits = (int)((Immediate)instruction.Operands[3]).Value;
            var signed = ((Immediate)instruction.Operands[4]).Value == 1;
            var mask = (1UL << sourceBits) - 1;
            var value = values[(IsilRegister)instruction.Operands[1]] & mask;
            if (signed && (value & (1UL << (sourceBits - 1))) != 0)
                value |= ~mask;
            values[(IsilRegister)instruction.Operands[0]] = resultBits == 32 ? value & uint.MaxValue : value;
        }
        throw new InvalidOperationException("Missing return");
    }

    private static List<NativeInstruction> Decode(string bytes, int bitness = 64)
    {
        var data = Convert.FromHexString(bytes);
        var decoder = Decoder.Create(bitness, new ByteArrayCodeReader(data));
        var result = new List<NativeInstruction>();
        while (decoder.IP < (ulong)data.Length)
            result.Add(decoder.Decode());
        return result;
    }
}
