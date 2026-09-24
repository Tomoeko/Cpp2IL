using System.Runtime.CompilerServices;

namespace ForwardedArgumentFixture
{
    public class ForwardTarget
    {
        public int Calls;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool TestAndCount(int value)
        {
            Calls = unchecked(Calls + 1);
            return value > 0;
        }
    }

    public class ForwardOwner
    {
        public int Prefix;
        public ForwardTarget Receiver;
        public int Suffix;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool Forward(int value, int ignored)
        {
            return Receiver.TestAndCount(value);
        }
    }
}
