using System.Runtime.CompilerServices;

namespace GuardedBoxedCastFixture
{
    public static class GuardedCast
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Matches(object value, int expected)
        {
            if (value is int)
                return (int)value == expected;
            return false;
        }
    }
}
