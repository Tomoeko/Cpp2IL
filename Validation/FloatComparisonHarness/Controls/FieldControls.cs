using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NarrowFieldControls
{
    public sealed class PlainFields
    {
        public bool Flag;
        public byte Value;
        public sbyte Signed;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsFlagZero() { return !Flag; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsByteZero() { return Value == 0; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsSignedZero() { return Signed == 0; }
    }

    public sealed class VolatileFields
    {
        public volatile bool Flag;
        public volatile byte Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsFlagZero() { return !Flag; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsByteZero() { return Value == 0; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public sealed class PackedFields
    {
        public byte Padding;
        public byte Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsByteZero() { return Value == 0; }
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct OverlappingFields
    {
        [FieldOffset(0)] public byte Value;
        [FieldOffset(0)] public int Overlap;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsByteZero() { return Value == 0; }
    }
}
