using System;
using System.Collections.Generic;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests.Isil;

public class X64NativeEntryRangeTests
{
    [Test]
    public void BoundaryEntriesRemainOutsideTheExclusiveInterior()
    {
        var entries = new Dictionary<ulong, List<MethodAnalysisContext>>
        {
            [0x1000] = [], [0x1010] = []
        };
        Assert.That(X64NativeInstructionReader.HasInteriorManagedEntry(entries, 0x1000, 0x1010), Is.False);
        entries.Add(0x1001, []);
        Assert.That(X64NativeInstructionReader.HasInteriorManagedEntry(entries, 0x1000, 0x1010), Is.True);
        entries.Remove(0x1001);
        Assert.That(X64NativeInstructionReader.HasInteriorManagedEntry(entries, 0x1000, 0x1010), Is.False,
            "Removing an entry must immediately restore the current evidence; no address index is cached.");
        entries.Add(0x100F, []);
        Assert.That(X64NativeInstructionReader.HasInteriorManagedEntry(entries, 0x1000, 0x1010), Is.True,
            "Even an empty binding at the last interior byte breaks a native body boundary.");
    }

    [TestCase(0UL, 16UL)]
    [TestCase(0x1000UL, 0x1000UL)]
    [TestCase(0x1000UL, 0x0FFFUL)]
    [TestCase(0x1000UL, 0x2001UL)]
    [TestCase(ulong.MaxValue - 7, 1UL)]
    public void InvalidWrappedAndUnboundedRangesCannotEstablishAnEmptyInterior(ulong start, ulong end)
    {
        Assert.That(X64NativeInstructionReader.HasInteriorManagedEntry(
            new Dictionary<ulong, List<MethodAnalysisContext>>(), start, end), Is.True);
    }

    [Test]
    public void MaximumBoundAndMaximumExclusiveEndpointAreFinite()
    {
        var entries = new Dictionary<ulong, List<MethodAnalysisContext>>();
        Assert.That(X64NativeInstructionReader.HasInteriorManagedEntry(entries, 0x1000, 0x2000), Is.False);
        Assert.That(X64NativeInstructionReader.HasInteriorManagedEntry(
            entries, ulong.MaxValue - 2, ulong.MaxValue), Is.False);
        entries.Add(ulong.MaxValue - 1, []);
        Assert.That(X64NativeInstructionReader.HasInteriorManagedEntry(
            entries, ulong.MaxValue - 2, ulong.MaxValue), Is.True);
    }
}
