using System.Runtime.CompilerServices;

namespace RegisterZeroExtensionFixture
{
    public static class ZeroExtensions
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int WidenByte(byte value)
        {
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int WidenWord(ushort value)
        {
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int AddByteAndBias(byte value, int bias)
        {
            return unchecked(value + bias);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int AddWordAndBias(ushort value, int bias)
        {
            return unchecked(value + bias);
        }
    }
}
