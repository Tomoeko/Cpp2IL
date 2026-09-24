using System.Runtime.CompilerServices;

namespace ByteThresholdFixture
{
    public sealed class ByteThresholdState
    {
        public byte Value;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool HasHighBit()
        {
            return Value >= 128;
        }
    }
}
