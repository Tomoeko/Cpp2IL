using System.Runtime.CompilerServices;

namespace FixedBooleanConjunctionFixture
{
    public class EmptyBase<T>
    {
    }

    public sealed class BooleanPair : EmptyBase<int>
    {
        public bool[] First;
        public bool[] Second;
        public int Sentinel;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool BothFalseAtZero()
        {
            return !First[0] && !Second[0];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool BothFalseAtOne()
        {
            return !First[1] && !Second[1];
        }
    }
}
