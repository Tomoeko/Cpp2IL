using System.Runtime.CompilerServices;

namespace NativeBooleanPredicateInvocationFixture
{
    public class Node
    {
        public int Calls;
        public bool Flag;
        public Node First;
        public Node Second;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetFlag(bool value)
        {
            Calls = unchecked(Calls + 1);
            Flag = value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ForwardNegated()
        {
            First.SetFlag(!Flag);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ForwardStoredPair(bool value)
        {
            Flag = value;
            First.SetFlag(Flag);
            Second.SetFlag(!Flag);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ForwardLivePair()
        {
            First.SetFlag(Flag);
            Second.SetFlag(!Flag);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ForwardComplementThenLive()
        {
            First.SetFlag(!Flag);
            Second.SetFlag(Flag);
        }
    }
}
