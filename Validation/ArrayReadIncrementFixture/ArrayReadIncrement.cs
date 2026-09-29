using System.Runtime.CompilerServices;

namespace ArrayReadIncrementFixture
{
    public static class ArrayReadIncrement
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReadThenIncrement(int[] values, int index)
        {
            return unchecked(values[index] + 1);
        }
    }
}
