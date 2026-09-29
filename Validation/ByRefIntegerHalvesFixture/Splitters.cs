using System.Runtime.CompilerServices;

namespace ByRefIntegerHalvesFixture
{
    public static class Splitters
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SplitSigned(long value, ref int low, ref int high)
        {
            low = unchecked((int)value);
            high = unchecked((int)(value >> 32));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SplitUnsigned(ulong value, ref uint low, ref uint high)
        {
            low = unchecked((uint)value);
            high = unchecked((uint)(value >> 32));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SplitUnsignedToSigned(ulong value, out int low, out int high)
        {
            low = unchecked((int)value);
            high = unchecked((int)(value >> 32));
        }
    }

    public sealed class SplitState
    {
        public int Neighbor;
        public object Reference;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SplitSigned(long value, ref int low, ref int high)
        {
            low = unchecked((int)value);
            high = unchecked((int)(value >> 32));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SplitUnsigned(ulong value, ref uint low, ref uint high)
        {
            low = unchecked((uint)value);
            high = unchecked((uint)(value >> 32));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SplitUnsignedToSigned(ulong value, out int low, out int high)
        {
            low = unchecked((int)value);
            high = unchecked((int)(value >> 32));
        }
    }
}
