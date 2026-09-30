using System.Runtime.CompilerServices;

namespace ScalarPositiveZeroLeafFixture
{
    public static class PositiveZeroStatics
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float SingleZero() { return 0f; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static double DoubleZero() { return 0d; }
    }

    public sealed class PositiveZeroReceiver
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public float SingleZero() { return 0f; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public double DoubleZero() { return 0d; }
    }
}
