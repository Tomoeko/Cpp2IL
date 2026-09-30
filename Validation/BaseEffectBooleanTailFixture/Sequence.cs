using System.Runtime.CompilerServices;

namespace BaseEffectBooleanTailFixture
{
    public class SequenceBase
    {
        public SequenceTarget Target;
        public int EffectCount;
        public int ProducerCount;
        public int Order;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public SequenceBase()
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual void Apply()
        {
            EffectCount++;
            Order = unchecked(Order * 10 + 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        protected SequenceTarget GetTarget()
        {
            ProducerCount++;
            Order = unchecked(Order * 10 + 2);
            return Target;
        }
    }

    public class EnableSequence : SequenceBase
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public EnableSequence()
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void Apply()
        {
            base.Apply();
            GetTarget().Enabled = true;
        }
    }

    public class DisableSequence : SequenceBase
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public DisableSequence()
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void Apply()
        {
            base.Apply();
            GetTarget().Enabled = false;
        }
    }

    public sealed class SequenceTarget
    {
        public bool Flag;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public SequenceTarget()
        {
        }

        public bool Enabled
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Flag; }
            [MethodImpl(MethodImplOptions.NoInlining)]
            set { Flag = value; }
        }
    }

    // The unrelated setter provides a redistributable identical-code-folding
    // witness. Its declaring type must never become the recovered call target.
    public sealed class FoldedTarget
    {
        public bool Flag;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public FoldedTarget()
        {
        }

        public bool Enabled
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Flag; }
            [MethodImpl(MethodImplOptions.NoInlining)]
            set { Flag = value; }
        }
    }
}
