using System.Runtime.CompilerServices;

namespace GuardedArrayLengthFixture
{
    public sealed class LengthState
    {
        public int[] Values;
        public int Index;
        public int Marker;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadParameter(int[] values)
        {
            Marker++;
            return values.Length;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadObjects(object[] values)
        {
            Marker++;
            return values.Length;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void CopyLength()
        {
            var captured = Values;
            Marker++;
            Index = captured.Length;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Advance()
        {
            var captured = Values;
            Index++;
            if (Index >= captured.Length) Index = 0;
            Marker++;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void BeforeLast()
        {
            var captured = Values;
            Index++;
            var last = captured.Length - 1;
            if (Index > last) Index = 0;
            Marker += 2;
        }
    }
}
