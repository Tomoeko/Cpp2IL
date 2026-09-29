using System.Runtime.CompilerServices;

namespace ConditionalBooleanStoreFixture
{
    public sealed class StoreState
    {
        public bool Enabled;
        public byte Value;
        public int Counter;
        public bool ResultFlag;
        public int Neighbor;
        public object Reference;
    }

    public static class ConditionalStores
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static StoreState WriteByteWhenEnabled(StoreState state, byte value)
        {
            if (state != null && state.Enabled)
                state.Value = value;
            return state;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static StoreState WritePairWhenEnabled(StoreState state, int counter, bool resultFlag)
        {
            if (state != null && state.Enabled)
            {
                state.Counter = counter;
                state.ResultFlag = resultFlag;
            }
            return state;
        }
    }
}
