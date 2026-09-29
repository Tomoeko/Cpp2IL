using System;
using Cpp2IL.Core.Analysis;

namespace Cpp2IL.Core.Tests.Analysis;

public class OrderedFieldStorageProofTests
{
    [TestCase(20)]
    [TestCase(24)]
    [TestCase(64)]
    [TestCase(long.MaxValue)]
    public void AnAuthenticatedLaterStartDoesNotNeedAnInventedExtent(long otherStart)
    {
        Assert.That(NarrowFieldEqualityProof.HasNonoverlappingStorage(16, 4, otherStart,
            () => throw new InvalidOperationException("A later field's extent is unavailable.")), Is.True);
    }

    [TestCase(0)]
    [TestCase(15)]
    [TestCase(16)]
    [TestCase(19)]
    public void UnknownEarlierOrIntersectingStorageRemainsUnresolved(long otherStart)
    {
        Assert.That(NarrowFieldEqualityProof.HasNonoverlappingStorage(16, 4, otherStart, () => 0), Is.False);
        Assert.That(NarrowFieldEqualityProof.HasNonoverlappingStorage(16, 4, otherStart, () => -1), Is.False);
    }

    [TestCase(12, 4, true)]
    [TestCase(12, 5, false)]
    [TestCase(15, 1, true)]
    [TestCase(15, 2, false)]
    [TestCase(16, 4, false)]
    [TestCase(19, 1, false)]
    public void EarlierKnownStorageMustEndBeforeTheAccess(long otherStart, long otherSize, bool disjoint)
    {
        Assert.That(NarrowFieldEqualityProof.HasNonoverlappingStorage(16, 4, otherStart, () => otherSize), Is.EqualTo(disjoint));
    }

    [TestCase(-1, 4, 20)]
    [TestCase(16, 0, 20)]
    [TestCase(16, -1, 20)]
    [TestCase(16, 4, -1)]
    [TestCase(long.MaxValue, 1, long.MaxValue)]
    [TestCase(long.MaxValue - 1, 4, long.MaxValue)]
    public void InvalidOrOverflowingOffsetsDoNotEstablishAnOrderedSpan(long start, long size, long otherStart)
    {
        Assert.That(NarrowFieldEqualityProof.HasNonoverlappingStorage(start, size, otherStart, () => 4), Is.False);
    }

    [Test]
    public void OverflowingEarlierExtentCannotWrapAroundToAppearDisjoint()
    {
        Assert.That(NarrowFieldEqualityProof.HasNonoverlappingStorage(16, 4, 15, () => long.MaxValue), Is.False);
    }
}
