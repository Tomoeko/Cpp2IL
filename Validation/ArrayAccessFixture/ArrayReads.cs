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
    }

    public static class ArrayWrites
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Write(int[] values, int index, int value)
        {
            values[index] = value;
        }
    }
}
