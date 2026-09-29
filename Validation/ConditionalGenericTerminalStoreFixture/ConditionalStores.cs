using System.Runtime.CompilerServices;

namespace ConditionalGenericTerminalStoreFixture
{
    public class StoreResult
    {
    }

    public sealed class GenericStoreState<T> : StoreResult
    {
        // These unrelated instance fields put the later Boolean and Int32
        // fields beyond the short x64 displacement range in a controlled build.
        public long Padding00;
        public long Padding01;
        public long Padding02;
        public long Padding03;
        public long Padding04;
        public long Padding05;
        public long Padding06;
        public long Padding07;
        public long Padding08;
        public long Padding09;
        public long Padding10;
        public long Padding11;
        public long Padding12;
        public long Padding13;
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
        public static StoreResult WriteFlagBlock(GenericStoreState<string> state,
            bool resultFlag)
        {
            if (state != null && state.Enabled)
                state.ResultFlag = resultFlag;
            return state;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static StoreResult WriteFlagEarly(GenericStoreState<string> state,
            bool resultFlag)
        {
            if (state == null)
                return state;
            if (!state.Enabled)
                return state;
            state.ResultFlag = resultFlag;
            return state;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static StoreResult WritePairBlock(GenericStoreState<string> state,
            CounterValue counter, bool resultFlag)
        {
            if (state != null && state.Enabled)
            {
                state.Counter = counter.Value;
                state.ResultFlag = resultFlag;
            }
            return state;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static StoreResult WritePairEarly(GenericStoreState<string> state,
            CounterValue counter, bool resultFlag)
        {
            if (state == null)
                return state;
            if (!state.Enabled)
                return state;
            state.Counter = counter.Value;
            state.ResultFlag = resultFlag;
            return state;
        }
    }
}
