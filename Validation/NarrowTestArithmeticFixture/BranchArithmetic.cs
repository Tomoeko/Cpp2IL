using System.Runtime.CompilerServices;

namespace NarrowTestArithmeticFixture
{
    public static class BranchArithmetic
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Select(int value, bool add)
        {
            if (add)
                return unchecked(value + 17);
            return unchecked(value - 17);
        }
    }
}
