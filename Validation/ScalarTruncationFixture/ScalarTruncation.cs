using System.Runtime.CompilerServices;

namespace ScalarTruncationFixture
{
    public static class ScalarTruncation
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ToInt32(double value) { return unchecked((int)value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long ToInt64(double value) { return unchecked((long)value); }
    }
}
