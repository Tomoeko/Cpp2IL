using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace NativeInstanceByrefThrowFixture
{
    public sealed class ThrowCases
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public byte[] ThrowSingle(ref int value)
        {
            throw new NotSupportedException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ThrowThree(ref List<int> first, ref List<int> second, ref List<int> third)
        {
            throw new NotSupportedException();
        }
    }
}
