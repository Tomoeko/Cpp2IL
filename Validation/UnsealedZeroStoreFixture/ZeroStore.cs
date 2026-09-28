using System.Runtime.CompilerServices;

namespace UnsealedZeroStoreFixture
{
    public class StoreBox
    {
        public int Count;
        public int Neighbor;
    }

    public class StoreOwner
    {
        public int Prefix;
        public StoreBox Box;
        public int Suffix;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Clear()
        {
            Box.Count = 0;
        }
    }
}
