using System.Runtime.CompilerServices;

namespace OwnerEffectArrayElementStoreFixture
{
    public sealed class Cell
    {
        public byte Before;
        public bool Enabled;
        public byte After;
    }

    public sealed class Catalog
    {
        public int Before;
        public bool Active;
        public byte After;
        public Cell[] Items;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Clear(int index)
        {
            Active = false;
            Items[index].Enabled = false;
        }
    }
}
