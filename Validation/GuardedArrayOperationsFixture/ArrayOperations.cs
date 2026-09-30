using System.Runtime.CompilerServices;

namespace GuardedArrayOperationsFixture
{
    public sealed class ArrayOperations
    {
        public int[] Values;
        public ArrayNode[] Nodes;
        public int Counter;
        public int Observed;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ArrayOperations() { }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadTwiceAfterEffect(int index)
        {
            var first = Values[index];
            Mark();
            return first + Values[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Mark()
        {
            Counter++;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void TouchNode(int index, int value)
        {
            Nodes[index].SetValue(value);
            Counter++;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadThenSet(int readIndex, int writeIndex, int value)
        {
            var captured = Values[readIndex];
            Observed = captured;
            Values[writeIndex] = value;
            return captured;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Sum(int[] left, int[] right, int index)
        {
            var first = left[index];
            return first + right[index];
        }
    }

    public sealed class ArrayNode
    {
        public int Value;
        public int Calls;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ArrayNode() { }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadValue()
        {
            return Value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetValue(int value)
        {
            Value = value;
            Calls++;
        }
    }
}
