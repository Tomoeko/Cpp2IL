using System.Runtime.CompilerServices;

namespace ZeroArgFieldCallFixture
{
    public class CallReceiver
    {
        public int Calls;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Touch()
        {
            Calls = unchecked(Calls + 1);
        }
    }

    public class CallOwner
    {
        public int Prefix;
        public CallReceiver Receiver;
        public int Suffix;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Forward()
        {
            Receiver.Touch();
        }
    }
}
