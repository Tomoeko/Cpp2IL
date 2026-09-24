using System.Runtime.CompilerServices;

namespace NarrowArrayFixture
{
    public static class NarrowArrayAccess
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static byte ReadByte(byte[] values, int index)
        {
            return values[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static sbyte ReadSignedByte(sbyte[] values, int index)
        {
            return values[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void WriteByte(byte[] values, int index, byte value)
        {
            values[index] = value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void WriteSignedByte(sbyte[] values, int index, sbyte value)
        {
            values[index] = value;
        }
    }
}
