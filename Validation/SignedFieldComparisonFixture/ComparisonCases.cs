using System;
using System.Runtime.CompilerServices;

namespace SignedFieldComparisonFixture
{
    public class ComparisonItem
    {
        public int Key;
        public int Neighbor;
    }

    public class PaddedComparisonItem
    {
        public Guid Tag;
        public long First;
        public long Second;
        public long Third;
        public long Fourth;
        public long Fifth;
        public long Sixth;
        public long Seventh;
        public int Key;
    }

    public class ComparisonOwner
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Compare(ComparisonItem first, ComparisonItem second)
        {
            if (first.Key < second.Key)
                return -1;
            return first.Key > second.Key ? 1 : 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int CompareAgain(ComparisonItem first, ComparisonItem second)
        {
            if (first.Key < second.Key)
                return -1;
            return first.Key > second.Key ? 1 : 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ComparePadded(PaddedComparisonItem first, PaddedComparisonItem second)
        {
            if (first.Key < second.Key)
                return -1;
            return first.Key > second.Key ? 1 : 0;
        }
    }
}
