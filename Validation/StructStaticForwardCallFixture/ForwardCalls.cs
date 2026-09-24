using System.Runtime.CompilerServices;

namespace StructStaticForwardCallFixture
{
    public static class InitializationWitness
    {
        public static int Events;
    }

    public struct FloatPair
    {
        public float First;
        public float Second;
        public static int Marker;
        public static float Bias;

        static FloatPair()
        {
            InitializationWitness.Events++;
            Marker = 37;
            Bias = -0.0f;
        }
    }

    public sealed class IgnoredToken
    {
        public int Marker;
    }

    public class ForwardTarget
    {
        public FloatPair Last;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool StorePair(FloatPair value)
        {
            Last = value;
            return true;
        }
    }

    public class ForwardOwner
    {
        public int Prefix;
        public ForwardTarget Receiver;
        public int Suffix;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool Forward(FloatPair value, IgnoredToken ignored)
        {
            return Receiver.StorePair(value);
        }
    }
}
