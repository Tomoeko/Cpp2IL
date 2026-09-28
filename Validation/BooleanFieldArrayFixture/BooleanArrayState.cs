using System.Runtime.CompilerServices;

namespace BooleanFieldArrayFixture
{
    public sealed class BooleanArrayState
    {
        public long Before;
        public bool[] Values;
        public long After;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetTrue(int index)
        {
            Values[index] = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetFalse(int index)
        {
            Values[index] = false;
        }
    }
}
