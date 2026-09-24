using System.Runtime.CompilerServices;

namespace ComposedArrayFixture
{
    public sealed class ArrayHolder
    {
        public object[] Items;
        public int Counter;
    }

    public sealed class ArrayReader
    {
        public ArrayHolder Holder;
        public object[] Replacement;
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
        private void ReplaceValues(ArrayHolder holder)
        {
            unchecked
            {
                Marker++;
            }
            holder.Items = Replacement;
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadAcrossReplacement(int firstIndex, int secondIndex)
        {
            var holder = Holder;
            var first = holder.Items[firstIndex];
            unchecked
            {
                holder.Counter++;
            }
            ReplaceValues(holder);
            var second = holder.Items[secondIndex];
            return first == second;
        }
    }
}
