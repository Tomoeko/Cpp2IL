using System.Runtime.CompilerServices;

namespace ConditionalGenericBooleanStoreFixture
{
    public class StoreResult
    {
    }

    public sealed class GenericStoreState<T> : StoreResult
    {
        public object Identity;
        public bool Enabled;
        public bool ResultFlag;
        public int Counter;
        public int Neighbor;
        public T Tag;
        public object NeighborReference;
    }

    public struct CounterValue
    {
        public int Value;
    }

    public static class ConditionalGenericStores
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static StoreResult WriteFlagWhenEnabled(GenericStoreState<string> state, bool resultFlag)
        {
            if (state != null && state.Enabled)
                state.ResultFlag = resultFlag;
            return state;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static StoreResult WritePairWhenEnabled(GenericStoreState<string> state,
            CounterValue counter, bool resultFlag)
        {
            if (state != null && state.Enabled)
            {
                state.Counter = counter.Value;
                state.ResultFlag = resultFlag;
            }
            return state;
        }
    }
}
