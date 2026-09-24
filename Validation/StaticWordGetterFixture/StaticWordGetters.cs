using System.Runtime.CompilerServices;

namespace StaticWordGetterFixture
{
    public static class StaticWordState
    {
        public static int Signed;
        public static uint Unsigned;
        public static int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReadSigned()
        {
            return Signed;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint ReadUnsigned()
        {
            return Unsigned;
        }
    }
}
