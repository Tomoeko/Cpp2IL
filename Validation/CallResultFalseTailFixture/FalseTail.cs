using System.Runtime.CompilerServices;

namespace CallResultFalseTailFixture
{
    public class TailNode
    {
        public bool LastValue;
        public int ApplyCount;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Apply(bool value)
        {
            LastValue = value;
            ApplyCount = unchecked(ApplyCount + 1);
        }
    }

    public class TailOwner
    {
        public TailNode Node;
        public int ProducerCount;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public TailNode GetNode()
        {
            ProducerCount = unchecked(ProducerCount + 1);
            return Node;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ForwardFalse()
        {
            GetNode().Apply(false);
        }
    }

    public class VirtualTailOwner : TailOwner
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual void VirtualForwardFalse()
        {
            GetNode().Apply(false);
        }
    }
}
