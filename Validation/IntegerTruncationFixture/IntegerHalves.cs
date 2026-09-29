using System.Runtime.CompilerServices;

namespace IntegerTruncationFixture
{
    public sealed class IntegerHalves
    {
        public int SignedLow;
        public int SignedHigh;
        public uint UnsignedLow;
        public uint UnsignedHigh;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int LowSigned(long value) { return unchecked((int)value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint LowUnsigned(ulong value) { return unchecked((uint)value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int HighSigned(long value) { return unchecked((int)(value >> 32)); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint HighUnsigned(ulong value) { return unchecked((uint)(value >> 32)); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SplitSigned(long value)
        {
            SignedLow = unchecked((int)value);
            SignedHigh = unchecked((int)(value >> 32));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SplitUnsigned(ulong value)
        {
            UnsignedLow = unchecked((uint)value);
            UnsignedHigh = unchecked((uint)(value >> 32));
        }
    }
}
