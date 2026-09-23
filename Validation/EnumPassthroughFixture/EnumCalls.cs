using System.Runtime.CompilerServices;

namespace EnumPassthroughFixture
{
    public enum Mode : int
    {
        Negative = -1,
        Zero = 0,
        Positive = 1
    }

    public sealed class EnumReceiver
    {
        public long Neighbor;
        public int Last;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Accept(Mode value)
        {
            Last = (int)value ^ 0x55555555;
        }
    }

    public sealed class EnumForwarder
    {
        public EnumReceiver Receiver;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Forward(Mode value)
        {
            Receiver.Accept(value);
        }
    }
}
