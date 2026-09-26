using System.Runtime.CompilerServices;

namespace ArrayElementStoreFixture
{
    public sealed class Cell
    {
        public byte Before;
        public bool Enabled;
        public byte After;
    }

    public sealed class CellCatalog
    {
        public long Before;
        public Cell[] Items;
        public long After;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Enable(int index)
        {
            Items[index].Enabled = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Disable(int index)
        {
            Items[index].Enabled = false;
        }
    }
}
