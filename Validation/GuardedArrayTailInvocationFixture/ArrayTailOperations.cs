using System.Runtime.CompilerServices;

namespace GuardedArrayTailInvocationFixture
{
    public sealed class ArrayTailValue
    {
        public int Calls;
        public int Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ArrayTailValue() { }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Read()
        {
            Calls++;
            return Value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Accept(int value)
        {
            Calls++;
            Value = value;
        }
    }

    public sealed class ArrayTailOperations
    {
        public ArrayTailValue[] Values;
        public int Marker;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ArrayTailOperations() { }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ArrayTailValue[] GetValues()
        {
            Marker++;
            return Values;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadTail(int index)
        {
            return Values[index].Read();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void AcceptTail(int index, int value)
        {
            Values[index].Accept(value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadAfterMarker(int index)
        {
            Marker++;
            return Values[index].Read();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadProducedTail(int index)
        {
            return GetValues()[index].Read();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReadParameterTail(ArrayTailValue[] values, int index)
        {
            return values[index].Read();
        }
    }
}
