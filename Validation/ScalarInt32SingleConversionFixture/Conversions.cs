using System.Runtime.CompilerServices;

namespace ScalarInt32SingleConversionFixture
{
    public enum ConversionChoice : int
    {
        Zero = 0,
        One = 1,
        Two = 2
    }

    public class ConversionHolder
    {
        public int Count;
        public int Divisor;
        public ConversionChoice Choice;
        public float First;
        public float Second;
        public int[] Samples;
        public int[][] Batches;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StoreFirst(int value)
        {
            First = value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StoreSecond(int value)
        {
            Second = value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public float Convert(int value)
        {
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float ConvertStatic(int value)
        {
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public float Ratio()
        {
            return (float)Count / (float)Divisor;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public float ScaledChoice()
        {
            return (float)unchecked((int)Choice - 1) * 0.5f;
        }
    }
}
