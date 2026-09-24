using System.Runtime.CompilerServices;

namespace StructForwardCallFixture
{
    public struct FloatPair
    {
        public float First;
        public float Second;
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
