using System.Runtime.CompilerServices;

namespace ObjectReferenceStoreFixture
{
    public sealed class ObjectHolder
    {
        public object Prefix;
        public object Item;
        public object Suffix;
        public int Sentinel;
    }

    public static class ObjectReferenceStores
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void StoreObject(ObjectHolder owner, object value)
        {
            owner.Item = value;
        }
    }
}
