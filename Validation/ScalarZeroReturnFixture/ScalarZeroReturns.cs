using System.Runtime.CompilerServices;

namespace ScalarZeroReturnFixture
{
    public static class ScalarZeroReturns
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float SingleZero() { return 0f; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static double DoubleZero() { return 0d; }
    }
}
