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
}
