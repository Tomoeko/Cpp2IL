using System.Runtime.CompilerServices;

namespace ParameterArrayFixture
{
    public sealed class ArrayReader
    {
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
        public bool CompareWithMark(object[] left, object[] right, int index)
        {
            var first = left[index];
            Mark();
            var second = right[index];
            return first == second;
        }
    }
}
