using System.Runtime.CompilerServices;

namespace ArrayAccessFixture
{
    public static class ArrayReads
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Read(int[] values, int index)
        {
            return values[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint ReadUnsigned(uint[] values, int index)
        {
            return values[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long ReadWide(long[] values, int index)
        {
            return values[index];
        }
    }

    public static class ArrayWrites
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Write(int[] values, int index, int value)
        {
            values[index] = value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void WriteUnsigned(uint[] values, int index, uint value)
        {
            values[index] = value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void WriteWide(long[] values, int index, long value)
        {
            values[index] = value;
        }
    }
}
