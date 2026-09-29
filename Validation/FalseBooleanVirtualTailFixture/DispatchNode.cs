using System.Runtime.CompilerServices;

namespace FalseBooleanVirtualTailFixture
{
    public class DispatchNode
    {
        public int Marker;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual void Mark(bool value)
        {
            Marker = 11;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ForwardFalse()
        {
            Mark(false);
        }
    }
}
