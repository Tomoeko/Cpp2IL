using System.Runtime.CompilerServices;

namespace ScalarFloatConversionFixture
{
    public static class ScalarConversions
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static double Widen(float value) { return value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float Narrow(double value) { return (float)value; }
    }
}
