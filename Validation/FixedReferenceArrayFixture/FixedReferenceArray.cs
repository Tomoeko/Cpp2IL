using System.Runtime.CompilerServices;

namespace FixedReferenceArrayFixture
{
    public sealed class Cell
    {
        public int Id;
        public long Before;
        public long After;
    }

    public sealed class CellCatalog
    {
        public long Before;
        public Cell[] Items;
        public long After;

        public Cell Third
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Items[2]; }
        }

        public Cell Fourth
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Items[3]; }
        }

        public Cell Fifth
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Items[4]; }
        }
    }
}
