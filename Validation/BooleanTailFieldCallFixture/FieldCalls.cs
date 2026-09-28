using System.Runtime.CompilerServices;

namespace BooleanTailFieldCallFixture
{
    public class BoolReceiver
    {
        public bool Enabled;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadEnabled()
        {
            return Enabled;
        }
    }

    public class BoolReceiverTwin
    {
        public bool Enabled;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadEnabled()
        {
            return Enabled;
        }
    }

    public class BoolOwner
    {
        public int Prefix;
        public BoolReceiver Receiver;
        public int Suffix;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ForwardRead()
        {
            return Receiver.ReadEnabled();
        }
    }
}
