using System.Runtime.CompilerServices;

namespace BooleanGetterFixture
{
    public sealed class FirstState
    {
        public bool Value;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadValue()
        {
            return Value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadGiven(FirstState state, int unused)
        {
            return state.Value;
        }
    }

    public sealed class SecondState
    {
        public bool Value;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadValue()
        {
            return Value;
        }
    }
}
