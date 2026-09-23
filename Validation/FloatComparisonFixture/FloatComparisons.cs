using System.Runtime.CompilerServices;

namespace FloatComparisonFixture
{
    public static class FloatComparisons
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Equal32(float left, float right) { return left == right; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool NotEqual32(float left, float right) { return left != right; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Less32(float left, float right) { return left < right; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool LessOrEqual32(float left, float right) { return left <= right; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Greater32(float left, float right) { return left > right; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool GreaterOrEqual32(float left, float right) { return left >= right; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Equal64(double left, double right) { return left == right; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool NotEqual64(double left, double right) { return left != right; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Less64(double left, double right) { return left < right; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool LessOrEqual64(double left, double right) { return left <= right; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Greater64(double left, double right) { return left > right; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool GreaterOrEqual64(double left, double right) { return left >= right; }
    }
}
