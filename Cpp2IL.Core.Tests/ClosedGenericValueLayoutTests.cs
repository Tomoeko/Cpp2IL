using Cpp2IL.Core.Analysis;

namespace Cpp2IL.Core.Tests;

public class ClosedGenericValueLayoutTests
{
    [TestCase(new long[] { 4, 4 }, 8, 4, new long[] { 0, 4 })]
    [TestCase(new long[] { 8, 4 }, 16, 8, new long[] { 0, 8 })]
    [TestCase(new long[] { 4, 8, 4 }, 24, 8, new long[] { 0, 8, 16 })]
    [TestCase(new long[] { 8, 4, 4 }, 16, 8, new long[] { 0, 8, 12 })]
    [TestCase(new long[] { 4, 4, 8 }, 16, 8, new long[] { 0, 4, 8 })]
    public void InflatedIntegerFieldsPreserveInteriorAndTrailingPadding(long[] sizes, long expectedSize,
        long alignment, long[] offsets)
    {
        var layout = ClosedGenericValueLayoutProof.ComputeIntegerLayout(sizes);
        Assert.That(layout, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(layout!.Size, Is.EqualTo(expectedSize));
            Assert.That(layout.Alignment, Is.EqualTo(alignment));
            Assert.That(layout.Offsets, Is.EqualTo(offsets));
        });
    }

    [TestCase(new long[] { })]
    [TestCase(new long[] { 0, 4 })]
    [TestCase(new long[] { 1, 4 })]
    [TestCase(new long[] { 2, 4 })]
    [TestCase(new long[] { 3, 8 })]
    [TestCase(new long[] { -1, 4 })]
    [TestCase(new long[] { long.MaxValue, 8 })]
    public void UnsupportedStorageIsNeverRoundedIntoAnInventedExtent(long[] sizes)
        => Assert.That(ClosedGenericValueLayoutProof.ComputeIntegerLayout(sizes), Is.Null);

    [Test]
    public void ExcessiveFieldListsRemainUnknown()
        => Assert.That(ClosedGenericValueLayoutProof.ComputeIntegerLayout(new long[65]), Is.Null);

    [Test]
    public void LayoutCopiesItsOffsetsAndDoesNotExposeMutableProofState()
    {
        var offsets = new long[] { 0, 4 };
        var layout = new ClosedGenericValueLayoutProof.Layout(8, 4, offsets);
        offsets[1] = 0;
        Assert.That(layout.Offsets, Is.EqualTo(new long[] { 0, 4 }));
        Assert.That(() => ((System.Collections.Generic.IList<long>)layout.Offsets)[1] = 0,
            Throws.TypeOf<System.NotSupportedException>());
    }
}
