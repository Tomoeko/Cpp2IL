using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SmallAggregateGetterFixture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SByteValue { public sbyte Value; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ByteValue { public byte Value; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Int16Value
    {
        public short Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static implicit operator short(Int16Value value) { return value.Value; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UInt16Value { public ushort Value; }

    public static class Getters
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static sbyte ReadSByte(SByteValue value) { return value.Value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static byte ReadByte(ByteValue value) { return value.Value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ushort ReadUInt16(UInt16Value value) { return value.Value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int WidenSByte(SByteValue value) { return value.Value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint WidenByte(ByteValue value) { return value.Value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int WidenInt16(Int16Value value) { return value.Value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint WidenUInt16(UInt16Value value) { return value.Value; }
    }
}
