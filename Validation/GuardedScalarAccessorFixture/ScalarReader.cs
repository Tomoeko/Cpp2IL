using System.Runtime.CompilerServices;

namespace GuardedScalarAccessorFixture
{
    public enum ScalarCode : int
    {
        Low = int.MinValue,
        NegativeOne = -1,
        Zero = 0,
        One = 1,
        High = int.MaxValue
    }

    public class ScalarState
    {
        private bool _flag;
        private ScalarCode _code;

        public int Neighbor;
        public object Reference;
        public int[] ArrayNeighbor;

        public bool Flag { get { return _flag; } }
        public ScalarCode Code { get { return _code; } }
    }

    public class ScalarHolder
    {
        protected ScalarState State;
    }

    public sealed class ScalarReader : ScalarHolder
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadFlag()
        {
            return State.Flag;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ScalarCode ReadCode()
        {
            return State.Code;
        }
    }
}
