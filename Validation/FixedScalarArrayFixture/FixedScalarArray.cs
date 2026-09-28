using System.Runtime.CompilerServices;

namespace FixedScalarArrayFixture
{
    public sealed class ScalarCatalog
    {
        public long Before;
        public int[] Values;
        public long After;
        public int State;

        public ScalarCatalog(int state)
        {
            State = state;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadSecond()
        {
            return Values[1];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadFourth()
        {
            return Values[3];
        }
    }
}
