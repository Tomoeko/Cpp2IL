using System.Runtime.CompilerServices;

namespace ReferenceStoreFixture
{
    public sealed class ReferenceNode
    {
        public ReferenceNode Prefix;
        public ReferenceNode Next;
        public ReferenceNode Suffix;
        public int Marker;
    }

    public static class ReferenceStores
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Store(ReferenceNode owner, ReferenceNode value)
        {
            owner.Next = value;
        }
    }
}
