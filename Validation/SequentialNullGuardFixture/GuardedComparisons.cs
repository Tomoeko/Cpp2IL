using System.Runtime.CompilerServices;

namespace SequentialNullGuardFixture
{
    public sealed class Box
    {
        public int Value;
        public int Neighbor;
    }

    public static class GuardedComparisons
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Compare(Box left, Box right)
        {
            int leftValue = left.Value;
            int rightValue = right.Value;
            if (leftValue < rightValue)
                return -1;
            if (leftValue > rightValue)
                return 1;
            return 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReadAfterAdd(Box box, int amount)
        {
            int adjusted = unchecked(amount + 7);
            return unchecked(adjusted + box.Value);
        }
    }
}
