using System.Runtime.CompilerServices;

namespace ShiftFixture
{
    public static class IntegerShifts
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Arithmetic32(int value, int count) { return value >> count; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint Logical32(uint value, int count) { return value >> count; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long Arithmetic64(long value, int count) { return value >> count; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ulong Logical64(ulong value, int count) { return value >> count; }
    }
}
