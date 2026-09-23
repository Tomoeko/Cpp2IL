using System.Runtime.CompilerServices;

namespace IntegerExtensionFixture
{
    public static class IntegerExtensions
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Sign8To32(sbyte value) { return value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Zero8To32(byte value) { return value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Sign16To32(short value) { return value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Zero16To32(ushort value) { return value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long Sign32To64(int value) { return value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ulong Zero32To64(uint value) { return value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long Sign8To64(sbyte value) { return value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ulong Zero8To64(byte value) { return value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long Sign16To64(short value) { return value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ulong Zero16To64(ushort value) { return value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint Sign8ToU32(sbyte value) { return unchecked((uint)value); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ulong Sign32ToU64(int value) { return unchecked((ulong)value); }
    }
}
