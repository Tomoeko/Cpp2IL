using System.Runtime.CompilerServices;

namespace OwnerIndexedEnumArrayFixture
{
    public enum Tone : int
    {
        Negative = -7,
        HighBit = unchecked((int)0x80000011),
        Positive = 42
    }

    public sealed class ReaderA
    {
        public long Before;
        public Tone[] Values;
        public int Slot;
        public long After;

        public Tone Current
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Values[Slot]; }
        }
    }

    public sealed class ReaderB
    {
        public long Before;
        public long Spacer;
        public Tone[] Values;
        public int Slot;
        public long After;

        public Tone Current
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Values[Slot]; }
        }
    }
}
