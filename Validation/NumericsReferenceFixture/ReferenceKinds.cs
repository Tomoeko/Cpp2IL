using System.Numerics;
using System.Runtime.CompilerServices;

namespace NumericsReferenceFixture
{
    public static class ReferenceKinds
    {
        public static BigInteger Number;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Increment(int value)
        {
            return value + 1;
        }
    }
}
