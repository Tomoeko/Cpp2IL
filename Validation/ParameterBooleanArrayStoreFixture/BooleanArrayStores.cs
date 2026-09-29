using System.Runtime.CompilerServices;

namespace ParameterBooleanArrayStoreFixture
{
    public sealed class BooleanArrayWriter
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Set(bool[] values, int index, bool value) { values[index] = value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Clear(bool[] values, int index) { values[index] = false; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Fill(bool[] values, int index) { values[index] = true; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ClearIgnoringValue(bool[] values, int index, bool unused) { values[index] = false; }
    }

    public static class BooleanArrayFunctions
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Set(bool[] values, int index, bool value) { values[index] = value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Clear(bool[] values, int index) { values[index] = false; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Fill(bool[] values, int index) { values[index] = true; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ClearIgnoringValue(bool[] values, int index, bool unused) { values[index] = false; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SetReordered(bool value, bool[] values, int index) { values[index] = value; }
    }
}
