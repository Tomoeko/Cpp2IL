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
