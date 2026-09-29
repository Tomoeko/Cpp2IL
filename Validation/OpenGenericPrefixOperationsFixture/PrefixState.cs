using System.Runtime.CompilerServices;

namespace OpenGenericPrefixOperationsFixture
{
    public sealed class PrefixState<T>
    {
        public int First;
        public int Second;
        public object Anchor;
        public T Later;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int UpdateAndSum(int delta)
        {
            First = unchecked(First + delta);
            return unchecked(First + Second);
        }
    }
}
