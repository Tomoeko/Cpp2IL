using System.Runtime.CompilerServices;

namespace ClosedGenericStorageFixture
{
    public struct Tail<T>
    {
        public T Item;
        public int Stamp;
    }

    public sealed class IntState
    {
        public Tail<int> Prefix;
        public int Value;
        public object Reference;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public IntState(int value) { Value = value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadValue() { return Value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Clear() { Value = 0; }
    }

    public sealed class LongState
    {
        public Tail<long> Prefix;
        public int Value;
        public object Reference;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public LongState(int value) { Value = value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadValue() { return Value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Clear() { Value = 0; }
    }
}
