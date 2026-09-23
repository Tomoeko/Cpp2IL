using System.Runtime.CompilerServices;

namespace XmmSpillFixture
{
    public struct FloatPack
    {
        public float First;
        public float Second;
        public float Third;
        public float Fourth;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public float ClearAndReadFirst()
        {
            Second = 0f;
            return First;
        }
    }

    public static class FloatAcrossCall
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float Sum(FloatPack values)
        {
            var second = values.Second;
            var third = values.Third;
            var fourth = values.Fourth;
            return values.ClearAndReadFirst() + second + third + fourth;
        }
    }
}
