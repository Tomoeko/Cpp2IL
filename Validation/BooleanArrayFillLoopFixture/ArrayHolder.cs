using System.Runtime.CompilerServices;

namespace BooleanArrayFillLoopFixture
{
    public sealed class ArrayHolder
    {
        public bool[] Values;
        public int Neighbor;
        public bool Flag;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ArrayHolder()
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void FillFalse()
        {
            for (var index = 0; index < Values.Length; index++)
                Values[index] = false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Fill(bool value)
        {
            for (var index = 0; index < Values.Length; index++)
                Values[index] = value;
        }
    }
}
