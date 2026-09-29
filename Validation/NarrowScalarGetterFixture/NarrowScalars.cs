using System.Runtime.CompilerServices;

namespace NarrowScalarGetterFixture
{
    public struct NarrowScalars
    {
        public sbyte SignedByte;
        public byte UnsignedByte;
        public short SignedWord;
        public ushort UnsignedWord;
        public uint Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public sbyte ReadSignedByte() { return SignedByte; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public byte ReadUnsignedByte() { return UnsignedByte; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public short ReadSignedWord() { return SignedWord; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ushort ReadUnsignedWord() { return UnsignedWord; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int WidenSignedByte() { return SignedByte; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public uint WidenUnsignedByte() { return UnsignedByte; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int WidenSignedWord() { return SignedWord; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public uint WidenUnsignedWord() { return UnsignedWord; }
    }
}
