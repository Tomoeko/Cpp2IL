using System.Runtime.CompilerServices;

namespace ConditionalCallResultTailFixture
{
    public class TailNode
    {
        public bool Ready;
        public bool Value;
        public int PredicateCount;
        public int ApplyCount;
        public int LastInteger;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadFlag()
        {
            PredicateCount = unchecked(PredicateCount + 1);
            return Ready;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyBoolean(bool value)
        {
            Value = value;
            ApplyCount = unchecked(ApplyCount + 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyInteger(int value)
        {
            LastInteger = value;
            ApplyCount = unchecked(ApplyCount + 1);
        }
    }

    public class ProducerOwner
    {
        public TailNode First;
        public TailNode Second;
        public int ProducerCount;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        protected TailNode GetNode()
        {
            ProducerCount = unchecked(ProducerCount + 1);
            return ProducerCount == 1 ? First : Second;
        }
    }

    public class ConditionalOwner : ProducerOwner
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual void WhenReadyTrue()
        {
            if (GetNode().ReadFlag())
                GetNode().ApplyBoolean(true);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual void WhenReadyFalse()
        {
            if (GetNode().ReadFlag())
                GetNode().ApplyBoolean(false);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual void WhenNotReadyTrue()
        {
            if (!GetNode().ReadFlag())
                GetNode().ApplyBoolean(true);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual void WhenNotReadyFalse()
        {
            if (!GetNode().ReadFlag())
                GetNode().ApplyBoolean(false);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual void WhenReadyZero()
        {
            if (GetNode().ReadFlag())
                GetNode().ApplyInteger(0);
        }
    }
}
