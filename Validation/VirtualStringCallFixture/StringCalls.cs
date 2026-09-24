using System.Runtime.CompilerServices;

namespace VirtualStringCallFixture
{
    public class DerivedNode : MiddleNode
    {
        public int Detail;
    }

    public class NodeOwner
    {
        private ChainNode _current;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public DerivedNode GetNode()
        {
            return _current as DerivedNode;
        }
    }

    public class VirtualLabelOwner : NodeOwner
    {
        public virtual string Label
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return GetNode().Text + "neutral-key"; }
        }
    }
}
