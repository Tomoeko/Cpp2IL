using System;
using System.Runtime.CompilerServices;

namespace ConditionalManagedThrowFixture
{
    public static class ConditionalManagedThrow
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReturnOrThrow(bool fail, int value)
        {
            if (fail)
                throw new NotSupportedException();

            return unchecked(value + 1);
        }
    }
}
