using System;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64AggregateScalarOperandProofTests
{
    [TestCase(0)]
    [TestCase(4)]
    public void PrivateEightByteSpillProjectsOnlyTheSelectedSingleComponent(int component)
    {
        var body = Decode(component == 0 ? LowBody : HighBody);
        var first = X64AggregateScalarOperandProof.TryFind(body, body[2].IP);
        var second = X64AggregateScalarOperandProof.TryFind(body, body[4].IP);
        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Not.Null);
        Assert.That(first!.Store.Op1Register, Is.EqualTo(Register.RCX));
        Assert.That(second!.Store.Op1Register, Is.EqualTo(Register.RDX));
        Assert.That(first.ComponentOffset, Is.EqualTo(component));
        Assert.That(second.ComponentOffset, Is.EqualTo(component));
    }

    [TestCase(false, 0)]
    [TestCase(false, 4)]
    [TestCase(true, 0)]
    [TestCase(true, 4)]
    public void OwnedHomeSlotsAndDirectScalarMemoryComparisonsRetainBothComponentOrigins(bool home, int component)
    {
        var body = Decode(MemoryBody(home, component));
        var left = X64AggregateScalarOperandProof.TryFind(body, body[2].IP);
        var right = X64AggregateScalarOperandProof.TryFind(body, body[4].IP);
        Assert.That(left, Is.Not.Null);
        Assert.That(right, Is.Not.Null);
        Assert.That(left!.ComponentOffset, Is.EqualTo(component));
        Assert.That(right!.ComponentOffset, Is.EqualTo(component));
        Assert.That(right.Load.Code, Is.EqualTo(Code.Comiss_xmm_xmmm32));
    }

    [TestCase("48894C2418")] // return address
    [TestCase("48894C2440")] // beyond the caller-owned four home slots
    [TestCase("48894C2439")] // unaligned spill
    public void HomeSpillsCannotOverlapReturnAddressOrEscapeTheAbiStorage(string replacement)
    {
        var body = Decode(MemoryBody(true, 0));
        body[1] = Decode(replacement)[0];
        var address = body[0].IP;
        for (var index = 0; index < body.Length; index++)
        {
            body[index].IP = address;
            address = body[index].NextIP;
        }
        Assert.That(X64AggregateScalarOperandProof.TryFind(body, body[2].IP), Is.Null);
    }

    [TestCase("660F2F0424")] // Double comparator
    [TestCase("0F2F442401")] // unaligned component
    [TestCase("0F2F442418")] // return-address memory
    public void DirectMemoryCompareKeepsWidthAlignmentAndSpillContainment(string replacement)
    {
        var body = Decode(MemoryBody(true, 0));
        body[4] = Decode(replacement)[0];
        var address = body[0].IP;
        for (var index = 0; index < body.Length; index++)
        {
            body[index].IP = address;
            address = body[index].NextIP;
        }
        Assert.That(X64AggregateScalarOperandProof.TryFind(body, body[4].IP), Is.Null);
    }

    [TestCase(0, "4883EC08")] // second aggregate falls outside the allocated frame
    [TestCase(1, "890C24")] // a partial store cannot establish the aggregate
    [TestCase(1, "4C892424")] // unproved entry register
    [TestCase(2, "F30F10442401")] // unaligned component
    [TestCase(2, "F30F10442408")] // load before its defining store
    [TestCase(2, "F20F100424")] // double read
    [TestCase(3, "48890C24")] // overlapping write before the second read
    [TestCase(5, "660F2FC1")] // double comparison reads another width
    [TestCase(5, "0F58C1")] // packed use reads unproved lanes
    [TestCase(5, "E800000000")] // implicit call clobbers
    [TestCase(5, "488D0C24")] // private frame address escapes
    public void PartialOverlappingEscapingAndPackedShapesRemainUnproved(int index, string replacement)
    {
        var body = Decode(LowBody);
        var changed = body.Select((instruction, position) => position == index ? Decode(replacement)[0] : instruction).ToArray();
        var address = body[0].IP;
        for (var position = 0; position < changed.Length; position++)
        {
            changed[position].IP = address;
            address = changed[position].NextIP;
        }
        Assert.That(X64AggregateScalarOperandProof.TryFind(changed, changed[index == 3 ? 4 : 2].IP), Is.Null);
    }

    [Test]
    public void BranchBackIntoSpillPrefixAndChangedAddressesRemainUnproved()
    {
        var body = Decode(LowBody);
        var branch = Decode("EB00")[0];
        branch.IP = body[6].IP;
        branch.NearBranch64 = body[1].IP;
        var changed = body.ToArray();
        changed[6] = branch;
        for (var index = 7; index < changed.Length; index++)
            changed[index].IP = changed[index - 1].NextIP;
        Assert.That(X64AggregateScalarOperandProof.TryFind(changed, body[2].IP), Is.Null);
        body[2].IP++;
        Assert.That(X64AggregateScalarOperandProof.TryFind(body, body[2].IP), Is.Null);
    }

    private const string LowBody = "4883EC1848890C24F30F1004244889542408F30F104C24080F2FC10F97C04883C418C3";
    private const string HighBody = "4883EC1848890C24F30F104424044889542408F30F104C240C0F2FC10F97C04883C418C3";

    private static string MemoryBody(bool home, int component)
    {
        var leftStore = home ? "48894C2438" : "48890C24";
        var leftLoad = home ? (component == 0 ? "F30F10442438" : "F30F1044243C") :
            component == 0 ? "F30F100424" : "F30F10442404";
        var rightStore = home ? "48891424" : "4889542408";
        var comparison = home ? (component == 0 ? "0F2F0424" : "0F2F442404") :
            component == 0 ? "0F2F442408" : "0F2F44240C";
        return "4883EC18" + leftStore + leftLoad + rightStore + comparison + "0F97C04883C418C3";
    }

    private static Instruction[] Decode(string bytes)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(bytes)));
        decoder.IP = 0x1000;
        var result = new System.Collections.Generic.List<Instruction>();
        var end = decoder.IP + (ulong)bytes.Length / 2;
        while (decoder.IP < end)
            result.Add(decoder.Decode());
        return result.ToArray();
    }
}
