using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86DivisionProofTests
{
    [TestCase("31D241F7F0")] // xor edx,edx; div r8d
    [TestCase("31D249F7F0")] // xor edx,edx; div r8 (EDX write clears RDX)
    [TestCase("4831D249F7F0")]
    [TestCase("BA0000000041F7F0")]
    [TestCase("48C7C20000000049F7F0")]
    [TestCase("9941F7F8")] // cdq; idiv r8d
    [TestCase("489949F7F8")] // cqo; idiv r8
    [TestCase("994189C941F7F8")] // unrelated divisor copy is allowed
    [TestCase("31D28BC149F7F0")] // unsigned high zero does not depend on low half
    [TestCase("EB0031D241F7F0")] // an entry at the actual setup still executes it
    public void ProvesExactZeroOrMatchingSignExtension(string bytes)
    {
        var body = Decode(bytes);
        Assert.That(X86DivisionProof.FindSingleWidthDividends(body), Is.EquivalentTo(new[] { body.Last().IP }));
    }

    [TestCase("41F7F0")] // high half has no known definition
    [TestCase("41F7F8")]
    [TestCase("6631D241F7F0")] // partial high-half writes
    [TestCase("30D241F7F0")]
    [TestCase("B20141F7F0")]
    [TestCase("BA0100000041F7F0")]
    [TestCase("9941F7F0")] // a signed high half is not necessarily unsigned zero
    [TestCase("31D241F7F8")] // zero is not necessarily signed extension
    [TestCase("9949F7F8")] // CDQ is not a64-bit sign extension
    [TestCase("489941F7F8")] // CQO does not establish signed32-bit high bits
    [TestCase("998BC141F7F8")] // low half changed after sign extension
    [TestCase("99B00141F7F8")]
    [TestCase("31D2B20141F7F0")] // partial overwrite invalidates a full zero
    [TestCase("31D20F44D141F7F0")] // conditional high-half write
    [TestCase("31D2E80000000041F7F0")] // opaque call clobbers the proof
    [TestCase("EB0231D241F7F0")] // entry can bypass setup
    [TestCase("EB0231D29041F7F0")] // entry between setup and division
    [TestCase("31D26641F7F0")] //16-bit division is unsupported
    [TestCase("31D241F6F0")] //8-bit division is unsupported
    [TestCase("31D241F7F0FFE1")] // unbounded indirect entry
    [TestCase("31D2F731")] // div dword [rcx] needs a separate memory access-width proof
    [TestCase("31D248F731")] // div qword [rcx]
    [TestCase("99F739")] // idiv dword [rcx]
    [TestCase("489948F739")] // idiv qword [rcx]
    public void RejectsUnprovedHighHalfOrEntryPath(string bytes)
    {
        Assert.That(X86DivisionProof.FindSingleWidthDividends(Decode(bytes)), Is.Empty);
    }

    [TestCase("41F7F0")]
    [TestCase("41F7F8")]
    public void StandaloneDivisionCannotAssumeItsImplicitHighHalf(string bytes)
    {
        var lifted = new X86InstructionSet().GetIsilFromInstruction(Decode(bytes).Single());
        Assert.That(lifted.Single().OpCode, Is.EqualTo(ISIL.OpCode.NotImplemented));
    }

    private static List<Instruction> Decode(string bytes)
    {
        var data = Convert.FromHexString(bytes);
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(data));
        var result = new List<Instruction>();
        while (decoder.IP < (ulong)data.Length)
            result.Add(decoder.Decode());
        return result;
    }
}
