using System.Runtime.CompilerServices;

namespace ClosedGenericGuardedCallFixture
{
    public sealed class Box<T>
    {
        public int Counter;
        public T Tag;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Add(int delta)
        {
            Counter = unchecked(Counter + delta);
            return Counter;
        }
    }

    public sealed class Sink
    {
        public int Last;
        public int Neighbor;
        public object Reference;
    }

    public static class Calls
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int CallAndStore(Box<string> box, int delta, Sink sink)
        {
            var value = box.Add(delta);
            sink.Last = value;
            return value;
        }
    }
}
