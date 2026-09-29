using System.Runtime.CompilerServices;

namespace FixedBooleanConjunctionFixture
{
    public sealed class BooleanPair
    {
        public bool[] First;
        public bool[] Second;
        public int Sentinel;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool BothFalseAtZero()
        {
            return !First[0] && !Second[0];
        }
    }
}
