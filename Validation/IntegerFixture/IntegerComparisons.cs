using System.Runtime.CompilerServices;

namespace IntegerFixture
{
    public static class IntegerComparisons
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Less32(uint left, uint right) { return left < right; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Greater32(uint left, uint right) { return left > right; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool LessOrEqual32(uint left, uint right) { return left <= right; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool GreaterOrEqual32(uint left, uint right) { return left >= right; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Less64(ulong left, ulong right) { return left < right; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Greater64(ulong left, ulong right) { return left > right; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool LessOrEqual64(ulong left, ulong right) { return left <= right; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool GreaterOrEqual64(ulong left, ulong right) { return left >= right; }
    }
}
