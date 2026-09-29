using System.Runtime.CompilerServices;

namespace EnumIntegerConversionFixture
{
    public enum SByteCode : sbyte { Zero = 0, One = 1, High = sbyte.MinValue, All = -1 }
    public enum ByteCode : byte { Zero = 0, One = 1, High = 128, All = byte.MaxValue }
    public enum Int16Code : short { Zero = 0, One = 1, High = short.MinValue, All = -1 }
    public enum UInt16Code : ushort { Zero = 0, One = 1, High = 32768, All = ushort.MaxValue }

    public static class Conversions
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int SByteToInt32(SByteCode value) { return (int)value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint SByteToUInt32(SByteCode value) { return unchecked((uint)value); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ByteToInt32(ByteCode value) { return (int)value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint ByteToUInt32(ByteCode value) { return (uint)value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Int16ToInt32(Int16Code value) { return (int)value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint Int16ToUInt32(Int16Code value) { return unchecked((uint)value); }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int UInt16ToInt32(UInt16Code value) { return (int)value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint UInt16ToUInt32(UInt16Code value) { return (uint)value; }
    }
}
