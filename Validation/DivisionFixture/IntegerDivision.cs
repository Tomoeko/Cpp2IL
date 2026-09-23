using System.Runtime.CompilerServices;

namespace DivisionFixture
{
    public static class IntegerDivision
    {
        // The guards are part of this fixture's contract. General divide-by-zero and
        // signed-overflow exception recovery require their own acceptance evidence.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Quotient32(int left, int right)
        {
            if (right == 0) return 0;
            if (left == int.MinValue && right == -1) return int.MinValue;
            return left / right;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Remainder32(int left, int right)
        {
            if (right == 0 || (left == int.MinValue && right == -1)) return 0;
            return left % right;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint QuotientUnsigned32(uint left, uint right)
        {
            return right == 0 ? 0 : left / right;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint RemainderUnsigned32(uint left, uint right)
        {
            return right == 0 ? 0 : left % right;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long Quotient64(long left, long right)
        {
            if (right == 0) return 0;
            if (left == long.MinValue && right == -1) return long.MinValue;
            return left / right;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long Remainder64(long left, long right)
        {
            if (right == 0 || (left == long.MinValue && right == -1)) return 0;
            return left % right;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ulong QuotientUnsigned64(ulong left, ulong right)
        {
            return right == 0 ? 0 : left / right;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ulong RemainderUnsigned64(ulong left, ulong right)
        {
            return right == 0 ? 0 : left % right;
        }
    }
}
