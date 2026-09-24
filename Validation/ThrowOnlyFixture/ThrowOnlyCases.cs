using System;
using System.Runtime.CompilerServices;

namespace ThrowOnlyFixture
{
    public sealed class ThrowOnlyCases
    {
        public int Current;
        public int Neighbor;
        public int Calls;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ThrowNotSupported()
        {
            throw new NotSupportedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ThrowNotImplemented()
        {
            throw new NotImplementedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReturnAfterCall(int input)
        {
            int adjusted = AddSeven(input);
            Calls = unchecked(Calls + 1);
            Current = unchecked(Current + adjusted);
            return Current;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int AddSeven(int value)
        {
            return unchecked(value + 7);
        }
    }
}
