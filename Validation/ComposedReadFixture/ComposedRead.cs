using System.Runtime.CompilerServices;

namespace ComposedReadFixture
{
    public sealed class ArrayHolder
    {
        public object[] Items;
        public int Counter;
    }

    public sealed class ArrayReader
    {
        public ArrayHolder Holder;
        public int Marker;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Mark()
        {
            unchecked
            {
                Marker++;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadTwice(int firstIndex, int secondIndex)
        {
            var holder = Holder;
            var first = holder.Items[firstIndex];
            unchecked
            {
                holder.Counter++;
            }
            Mark();
            var second = holder.Items[secondIndex];
            return first == second;
        }
    }
}
