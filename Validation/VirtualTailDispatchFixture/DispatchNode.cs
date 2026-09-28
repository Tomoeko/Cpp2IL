using System.Runtime.CompilerServices;

namespace VirtualTailDispatchFixture
{
    public class DispatchNode
    {
        public int Marker;

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

    public sealed class DerivedNode : DispatchNode
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void Mark()
        {
            Marker = 29;
        }
    }
}
