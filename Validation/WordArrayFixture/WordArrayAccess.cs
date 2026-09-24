using System.Runtime.CompilerServices;

namespace WordArrayFixture
{
    public static class WordArrayAccess
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReadSigned(short[] values, int index)
        {
            return values[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReadUnsigned(ushort[] values, int index)
        {
            return values[index];
        }
    }
}
