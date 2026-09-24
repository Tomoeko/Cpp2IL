using System.Runtime.CompilerServices;

namespace AliasAmbiguityFixture
{
    public static class AliasMethods
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int First(int value)
        {
            return value + 7;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Second(int value)
        {
            return value + 7;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int CallFirst(int value)
        {
            return First(value);
        }
    }
}
