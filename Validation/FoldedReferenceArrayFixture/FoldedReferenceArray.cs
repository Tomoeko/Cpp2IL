using System.Runtime.CompilerServices;

namespace FoldedReferenceArrayFixture
{
    public sealed class Cell
    {
        public int Id;
        public long Before;
        public long After;
    }

    public sealed class CatalogA
    {
        public long Before;
        public Cell[] Items;
        public long After;

        public Cell First
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Items[0]; }
        }

        public Cell Second
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Items[1]; }
        }
    }

    public sealed class CatalogB
    {
        public long Before;
        public Cell[] Items;
        public long After;

        public Cell First
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Items[0]; }
        }

        public Cell Second
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Items[1]; }
        }
    }
}
