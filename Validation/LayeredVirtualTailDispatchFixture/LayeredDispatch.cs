using System.Runtime.CompilerServices;

namespace LayeredVirtualTailDispatchFixture
{
    public interface ITag
    {
        int ReadTag();
    }

    public class DispatchRoot
    {
        public int Marker;
        public int Neighbor;
    }

    public class LayeredNode : DispatchRoot, ITag
    {
        public int TagValue;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadTag()
        {
            return TagValue;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual void Mark()
        {
            Marker = 11;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Forward()
        {
            Mark();
        }
    }

    public sealed class LayeredOverride : LayeredNode
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void Mark()
        {
            Marker = 29;
        }
    }
}
