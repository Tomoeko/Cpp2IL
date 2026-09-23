using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86ShiftCountExtensionProofTests
{
    [TestCase("8BC183E21F0FB6CAD3F8C3")] // mov eax,ecx; and edx,31; movzx ecx,dl; sar eax,cl; ret
    [TestCase("8BC183E21F0FB6CAD3E8C3")]
    [TestCase("488BC183E23F0FB6CA48D3F8C3")]
    [TestCase("488BC183E23F0FB6CA48D3E8C3")]
    [TestCase("8BC181E21F0000000FB6CAD3E0C3")] // full immediate form, retained AND
    [TestCase("8BC10FB6CAD3E0C3")] // untouched low byte also suffices for masked shift counts
    [TestCase("488BC10FB6CA48D3E0C3")]
    [TestCase("8BC10FB6CAD3E848D3E0C3")] // multiple low-byte-only uses
    [TestCase("8BC10FB6CAD3E8B900000000C3")] // a full overwrite kills the old RCX value
    [TestCase("8BC10FB6CAD3E848C7C100000000C3")]
    [TestCase("8BC10FB6CAD3E8B900000000")] // dead count proved, missing method return remains a separate failure
    public void ProvesOnlyLowByteShiftCountUse(string bytes)
    {
        var body = Decode(bytes);
        var conversion = body.Single(i => i.Mnemonic == Mnemonic.Movzx);
        Assert.That(X86ShiftCountExtensionProof.Find(body), Is.EquivalentTo(new[] { conversion.IP }));
    }

    [TestCase("8BC131D20FB6CAD3E8C3")] // source replaced, not an incoming parameter
    [TestCase("8BC1B2800FB6CAD3E8C3")] // DL partial overwrite
    [TestCase("8BC166BA80000FB6CAD3E8C3")]
    [TestCase("8BC1990FB6CAD3E8C3")] // implicit EDX definition
    [TestCase("8BC10F44D10FB6CAD3E8C3")] // conditional source definition
    [TestCase("8BC16683E21F0FB6CAD3E8C3")] // partial DX mask
    [TestCase("8BC14883E21F0FB6CAD3E8C3")] // full RDX mask is outside the measured Int32 contract
    [TestCase("8BC181E2FF0000000FB6CAD3E8C3")] // unproved mask
    [TestCase("8BC183E2FF0FB6CAD3E8C3")] // signed immediate is not 31 or63
    [TestCase("8BC183E21F83E23F0FB6CAD3E8C3")] // only one tracked source transformation
    [TestCase("8BC10FB6CED3E8C3")] // DH high-byte source
    [TestCase("8BC1400FB6CCD3E8C3")] // SPL is not the managed count
    [TestCase("8BC1400FB6CDD3E8C3")] // BPL
    [TestCase("8BC10FB6C9D3E8C3")] // CL is the value parameter, not the count
    [TestCase("8BC10FB60AD3E8C3")] // memory source
    [TestCase("8BC1480FB6CAD3E8C3")] // R64 result is outside this count-only conversion
    [TestCase("8BC1660FB6CAD3E8C3")] // CX partial result
    [TestCase("EB008BC10FB6CAD3E8C3")] // prefix branch can bypass provenance
    [TestCase("E8000000008BC10FB6CAD3E8C3")] // prefix call clobbers argument registers
    [TestCase("8BC10FB6CAD3E8EB00C3")] // control ambiguity after the count use
    [TestCase("8BC10FB6CAD3E8E800000000C3")]
    [TestCase("8BC10FB6CAD3E84889C8C3")] // full RCX read escapes
    [TestCase("8BC10FB6CAD3E8488D01C3")] // address use escapes
    [TestCase("8BC10FB6CAD3E883F900C3")] // non-shift full ECX read
    [TestCase("8BC10FB6CAD3E80FB6D1C3")] // non-shift CL read
    [TestCase("8BC10FB6CAD3E9C3")] // destination ECX also reads full count register
    [TestCase("8BC10FB6CA48D3E9C3")]
    [TestCase("8BC10FB6CAD320C3")] // memory destination, even though count is CL
    [TestCase("8BC10FB6CA66D3E8C3")] //16-bit shift
    [TestCase("8BC10FB6CAD2E8C3")] //8-bit shift
    [TestCase("8BC10FB6CAD3E8B100C3")] // partial count register overwrite
    [TestCase("8BC10FB6CAD3E80F44C8C3")] // conditional overwrite is not a definite kill
    [TestCase("8BC10FB6CAD3E8")]
    [TestCase("8BC10FB6CAD3E8C20000")] // nonplain return
    [TestCase("8BC10FB6CAB900000000C3")] // no proved count use
    [TestCase("8BC10FB6CAC3")]
    [TestCase("8BC10FB6CAD3E8B900000000EBF3")] // overwrite cannot hide a backward entry
    public void RejectsUnprovedSourceConsumptionOrLiveOut(string bytes)
    {
        Assert.That(X86ShiftCountExtensionProof.Find(Decode(bytes)), Is.Empty);
    }

    [Test]
    public void ClosedPlainReturnExcludesUnreachableAdjacentInstructions()
    {
        var body = Decode("8BC183E21F0FB6CAD3E8C3CCFFE10FB6CAD3E8C3");
        Assert.That(X86ShiftCountExtensionProof.Find(body), Is.EquivalentTo(new[] { body[2].IP }));
    }

    [Test]
    public void RequiresContiguous64BitDecodedEntry()
    {
        var body = Decode("8BC10FB6CAD3E8C3");
        var conversion = body[1];
        conversion.IP++;
        body[1] = conversion;
        Assert.That(X86ShiftCountExtensionProof.Find(body), Is.Empty);
        Assert.That(X86ShiftCountExtensionProof.Find(Decode("8BC10FB6CAD3E8C3", 32)), Is.Empty);
    }

    private static List<Instruction> Decode(string bytes, int bitness = 64)
    {
        var data = Convert.FromHexString(bytes);
        var decoder = Decoder.Create(bitness, new ByteArrayCodeReader(data));
        var result = new List<Instruction>();
        while (decoder.IP < (ulong)data.Length)
            result.Add(decoder.Decode());
        return result;
    }
}
