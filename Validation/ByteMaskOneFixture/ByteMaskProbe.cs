using System;
using System.Runtime.CompilerServices;

namespace ByteMaskOneFixture
{
    [Flags]
    public enum FlagBits : byte
    {
        None = 0,
        One = 1
    }

    public static class ByteMaskProbe
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool HasOne(int unused, FlagBits bits) =>
            (byte)(bits & FlagBits.One) > 0;
    }
}
