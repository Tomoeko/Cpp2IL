using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86MethodBodyEstimateTests
{
    private const ulong Entry = 0x180001000;

    [Test]
    public void AnInBodyRvaInsideAnImmediateCannotTruncateTargetCode()
    {
        // Three NOPs put MOV's immediate at an aligned offset. Its integer
        // payload resembles a local RVA, but is part of a complete instruction.
        var bytes = Convert.FromHexString("909090B810100000C3");
        var length = X86Utils.EstimateMethodBodyLength(bytes, Entry, Entry + 0x20, true);
        var decoded = X86Utils.Iterate(bytes.AsSpan(0, length), Entry, false);
        Assert.Multiple(() =>
        {
            Assert.That(length, Is.EqualTo(bytes.Length));
            Assert.That(decoded.Select(instruction => instruction.Code),
                Is.EqualTo(new[] { Code.Nopd, Code.Nopd, Code.Nopd, Code.Mov_r32_imm32, Code.Retnq }));
            Assert.That(decoded[3].Immediate32, Is.EqualTo(0x1010));
            Assert.That(X86CallerExceptionRegionProof.Check(decoded, Entry, new HashSet<ulong>(),
                (start, end) => new(X64UnwindProof.SpanKind.NoEntry, start, end)), Is.Null);
        });
    }

    [Test]
    public void EstimatedTableBytesDoNotEstablishAnIndirectManagedSuccessor()
    {
        var bytes = Convert.FromHexString("FFE0909010100000");
        var length = X86Utils.EstimateMethodBodyLength(bytes, Entry, Entry + 0x20, true);
        var decoded = X86Utils.Iterate(bytes.AsSpan(0, length), Entry, false);
        Assert.That(length, Is.EqualTo(bytes.Length));
        Assert.That(X86CallerExceptionRegionProof.Check(decoded, Entry, new HashSet<ulong>(),
            (start, end) => new(X64UnwindProof.SpanKind.NoEntry, start, end)),
            Does.Contain("unproved exit"));
    }

    [Test]
    public void OtherProfilesRetainTheirCompleteEntryEstimate()
    {
        var bytes = Convert.FromHexString("9090909010100000");
        Assert.That(X86Utils.EstimateMethodBodyLength(bytes, Entry, Entry + 0x20, false),
            Is.EqualTo(4));
        Assert.That(X86Utils.EstimateMethodBodyLength(bytes, Entry, Entry + 0x20, true),
            Is.EqualTo(bytes.Length), "the target delegates code/data boundaries to the closed proof");
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    public void LegacyEstimationNeverReadsOutsideAShortDeclaredSpan(int length)
    {
        // The backing array continues with a plausible RVA. The declared span
        // cannot borrow those bytes to complete a jump-table entry.
        var bytes = Convert.FromHexString("909090901010000010100000");
        Assert.That(X86Utils.EstimateMethodBodyLength(bytes.AsSpan(0, length),
            Entry, Entry + 0x20, false), Is.EqualTo(length));
    }
}
