using System.Runtime.CompilerServices;

namespace OrderedCallTailGuardFixture
{
    public struct LayoutSpacing
    {
        public long A;
        public long B;
        public long C;
        public long D;
        public long E;
        public long F;
        public long G;
        public long H;
        public long I;
        public long J;
        public long K;
        public long L;
    }

    public abstract class GuardBase
    {
        public int MarkCount;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual void Mark()
        {
            MarkCount = unchecked(MarkCount + 1);
        }

        public abstract void Forward();
    }

    public class GuardNode
    {
        public LayoutSpacing Spacing;
        public bool Flag;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Apply(bool value)
        {
            Flag = value;
        }
    }

    public sealed class GuardOwner : GuardBase
    {
        public LayoutSpacing Spacing;
        public GuardNode Node;
        public int ProducerCount;
        public int OverrideCount;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void Mark()
        {
            OverrideCount = unchecked(OverrideCount + 10);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public GuardNode GetNode()
        {
            ProducerCount = unchecked(ProducerCount + 1);
            return Node;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void Forward()
        {
            base.Mark();
            GetNode().Apply(true);
        }
    }
}
