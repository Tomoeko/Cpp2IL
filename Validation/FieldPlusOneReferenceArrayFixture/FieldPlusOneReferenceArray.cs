using System.Runtime.CompilerServices;

namespace FieldPlusOneReferenceArrayFixture
{
    public sealed class Cell
    {
        public int Id;
    }

    public sealed class ReaderA
    {
        public long Before;
        public Cell[] Items;
        public int Position;
        public long After;

        public Cell Next
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Items[Position + 1]; }
        }
    }

    public sealed class ReaderB
    {
        public long Before;
        public long Spacer;
        public Cell[] Items;
        public int Position;
        public long After;

        public Cell Next
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Items[Position + 1]; }
        }
    }
}
