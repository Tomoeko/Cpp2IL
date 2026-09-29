using System.Runtime.CompilerServices;

namespace AggregateScalarCompareFixture
{
    public struct ScalarPair
    {
        public float Low;
        public float High;
    }

    public sealed class ScalarPairComparer
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool StaticLowGreater(ScalarPair first, ScalarPair second)
        {
            return first.Low > second.Low;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool StaticHighGreater(ScalarPair first, ScalarPair second)
        {
            return first.High > second.High;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool InstanceLowGreater(ScalarPair first, ScalarPair second)
        {
            return first.Low > second.Low;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool InstanceHighGreater(ScalarPair first, ScalarPair second)
        {
            return first.High > second.High;
        }
    }
}
