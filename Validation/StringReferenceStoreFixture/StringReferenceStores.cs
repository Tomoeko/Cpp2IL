using System.Runtime.CompilerServices;

namespace StringReferenceStoreFixture
{
    public sealed class TextHolder
    {
        public object Prefix;
        public string Text;
        public object Suffix;
        public int Sentinel;
    }

    public static class StringReferenceStores
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void StoreText(TextHolder owner, string value)
        {
            owner.Text = value;
        }
    }
}
