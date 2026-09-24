using System.Runtime.CompilerServices;

namespace ArraySequenceFixture
{
    public sealed class ArraySequence
    {
        public int[] Values;
        public int Counter;
        public int LastRead;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadThenWrite(int firstIndex, int secondIndex)
        {
            var value = Values[firstIndex];
            LastRead = value;
            unchecked
            {
                Counter++;
            }
            Values[secondIndex] = value;
            return value;
        }
    }
}
