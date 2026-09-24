using System.Runtime.CompilerServices;

namespace FieldArrayFixture
{
    public sealed class FieldArrayState
    {
        public int[] Values;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadFirst()
        {
            return Values[0];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadAt(int index)
        {
            return Values[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void WriteAt(int index, int value)
        {
            Values[index] = value;
        }
    }
}
