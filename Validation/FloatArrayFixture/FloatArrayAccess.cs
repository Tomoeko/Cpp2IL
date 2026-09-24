using System.Runtime.CompilerServices;

namespace FloatArrayFixture
{
    public static class FloatArrayAccess
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float Read(float[] values, int index)
        {
            return values[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Write(float[] values, int index, float value)
        {
            values[index] = value;
        }
    }
}
