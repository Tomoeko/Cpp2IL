using System.Runtime.CompilerServices;

namespace ReferenceNullFixture
{
    public sealed class ReferenceBox
    {
        public int Neighbor;
    }

    public static class ReferenceNullCases
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool ClassIsNull(ReferenceBox box)
        {
            return box == null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool ArrayHasValue(int[] values)
        {
            return values != null;
        }
    }
}
