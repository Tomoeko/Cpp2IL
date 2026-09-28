using System;
using System.Runtime.CompilerServices;

namespace ByteMaskParameterFixture
{
    [Flags]
    public enum FlagBits : byte
    {
        None = 0,
        Two = 2,
        Four = 4,
        Eight = 8,
        Sixteen = 16,
        ThirtyTwo = 32,
        SixtyFour = 64
    }

    public static class ByteMaskProbe
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool HasTwo(int unused, FlagBits bits) => (byte)(bits & FlagBits.Two) > 0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool HasFour(int unused, FlagBits bits) => (byte)(bits & FlagBits.Four) > 0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool HasEight(int unused, FlagBits bits) => (byte)(bits & FlagBits.Eight) > 0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool HasSixteen(int unused, FlagBits bits) => (byte)(bits & FlagBits.Sixteen) > 0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool HasThirtyTwo(int unused, FlagBits bits) => (byte)(bits & FlagBits.ThirtyTwo) > 0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool HasSixtyFour(int unused, FlagBits bits) => (byte)(bits & FlagBits.SixtyFour) > 0;
    }
}
