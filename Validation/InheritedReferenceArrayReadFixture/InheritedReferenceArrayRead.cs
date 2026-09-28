using System.Runtime.CompilerServices;

namespace InheritedReferenceArrayReadFixture
{
    public class Cell
    {
        public int Marker;
    }

    public sealed class DerivedCell : Cell
    {
    }

    public class BaseCatalog
    {
        public long Before;
        public Cell[] Items;
        public long After;
    }

    public sealed class DerivedCatalog : BaseCatalog
    {
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
