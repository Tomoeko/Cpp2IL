using System.Runtime.CompilerServices;

namespace OrderedGenericTailFieldFixture
{
    public struct Tail<T>
    {
        public T Item;
        public int Guard;
    }

    public sealed class State
    {
        public int Value;
        public Tail<long> Later;
        public object Reference;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public State(int value)
        {
            Value = value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadValue()
        {
            return Value;
        }
    }
}
