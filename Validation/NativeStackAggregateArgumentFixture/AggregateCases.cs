using System.Runtime.CompilerServices;

namespace NativeStackAggregateArgumentFixture
{
    public struct Triple
    {
        public float First;
        public float Second;
        public float Third;
    }

    public static class Calls
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float Sum(Triple value) { return (value.First + value.Second) + value.Third; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float Forward(Triple value) { return Sum(value) + 1f; }
    }
}
