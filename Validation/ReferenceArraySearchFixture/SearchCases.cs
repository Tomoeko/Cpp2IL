using System;
using System.Runtime.CompilerServices;

namespace ReferenceArraySearchFixture
{
    public class Entry
    {
        public int Key;
    }

    public class PaddedEntry
    {
        public Guid Tag;
        public string Text;
        public int Key;
    }

    public class SearchHolder
    {
        public Entry[] Items;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Find(int key)
        {
            var items = Items;
            if (items != null)
                for (var index = 0; index < items.Length; index++)
                    if (items[index] != null && items[index].Key == key)
                        return index;
            return -1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool Contains(int key)
        {
            var items = Items;
            var found = -1;
            if (items != null)
                for (var index = 0; index < items.Length; index++)
                    if (items[index] != null && items[index].Key == key)
                    {
                        found = index;
                        break;
                    }
            return found >= 0;
        }
    }

    public class PaddedSearchHolder
    {
        public int Neighbor;
        public PaddedEntry[] Items;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Find(int key)
        {
            var items = Items;
            if (items != null)
                for (var index = 0; index < items.Length; index++)
                    if (items[index] != null && items[index].Key == key)
                        return index;
            return -1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool Contains(int key)
        {
            var items = Items;
            var found = -1;
            if (items != null)
                for (var index = 0; index < items.Length; index++)
                    if (items[index] != null && items[index].Key == key)
                    {
                        found = index;
                        break;
                    }
            return found >= 0;
        }
    }
}
