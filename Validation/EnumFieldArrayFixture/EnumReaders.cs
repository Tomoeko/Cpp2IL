using System.Runtime.CompilerServices;

namespace EnumFieldArrayFixture
{
    public enum SignedTone : int
    {
        Negative = -7,
        Zero = 0,
        HighBit = unchecked((int)0x80000011),
        Positive = 42
    }

    public enum UnsignedTone : uint
    {
        Zero = 0,
        HighBit = 0x80000011u,
        Maximum = uint.MaxValue,
        Positive = 42
    }

    public sealed class SignedReader
    {
        public long Before;
        public SignedTone[] Values;
        public long After;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public SignedTone ReadFirst() { return Values[0]; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public SignedTone ReadFixed() { return Values[2]; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public SignedTone ReadAt(int index) { return Values[index]; }
    }

    public sealed class UnsignedReader
    {
        public long Before;
        public long Spacer;
        public UnsignedTone[] Values;
        public long After;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public UnsignedTone ReadFirst() { return Values[0]; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public UnsignedTone ReadFixed() { return Values[2]; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public UnsignedTone ReadAt(int index) { return Values[index]; }
    }
}
