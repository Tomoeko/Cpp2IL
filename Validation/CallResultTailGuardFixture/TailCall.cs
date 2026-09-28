using System.Runtime.CompilerServices;

namespace CallResultTailGuardFixture
{
    public class TailNode
    {
        public int LastValue;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Apply(int value)
        {
            LastValue = value;
        }
    }

    public class TailOwner
    {
        public TailNode Node;
        public int ProducerCount;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public TailNode GetNode()
        {
            ProducerCount = 1;
            return Node;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Forward()
        {
            GetNode().Apply(7);
        }
    }
}
