using System.Runtime.CompilerServices;

namespace TypedFieldAddressFixture
{
    public struct CounterCell
    {
        public long Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public long Advance(long amount)
        {
            Value = unchecked(Value + amount);
            return Value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Reset()
        {
            Value = 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public long Read()
        {
            return Value;
        }
    }

    public struct SmallCell
    {
        public int Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Advance(int amount)
        {
            Value = unchecked(Value + amount);
            return Value;
        }
    }

    public sealed class StorageOwner
    {
        public CounterCell First;
        public CounterCell Second;
        public SmallCell Small;
        public bool Enabled;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public long AdvanceFirst(long amount)
        {
            return First.Advance(amount);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public long AdvanceSecond(long amount)
        {
            return Second.Advance(amount);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int AdvanceSmall(int amount)
        {
            return Small.Advance(amount);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ResetFirst()
        {
            First.Reset();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public long ReadFirst()
        {
            return First.Read();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public long GuardedAdvance(long amount)
        {
            if (Enabled)
                return First.Advance(amount);
            return 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public long AdvanceTwice(long amount)
        {
            First.Advance(amount);
            return First.Advance(amount);
        }
    }
}
