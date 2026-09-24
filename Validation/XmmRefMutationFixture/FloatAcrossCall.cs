using System.Runtime.CompilerServices;

namespace XmmRefMutationFixture
{
    public struct FloatPack
    {
        public float Second;
        public float Third;
        public float Fourth;
    }

    public static class FloatAcrossCall
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float Identity(ref FloatPack values, float value)
        {
            values.Second = 0f;
            values.Third = 0f;
            values.Fourth = 0f;
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float Sum(float first, FloatPack values)
        {
            var second = values.Second;
            var third = values.Third;
            var fourth = values.Fourth;
            return Identity(ref values, first) + second + third + fourth;
        }
    }
}
