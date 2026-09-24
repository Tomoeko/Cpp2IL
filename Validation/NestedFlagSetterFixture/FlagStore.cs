using System.Runtime.CompilerServices;

namespace NestedFlagSetterFixture
{
    public sealed class Payload
    {
        public int Marker;
    }

    public sealed class FlagChild
    {
        public int Neighbor;
        public bool Flag;
        public object After;
    }

    public sealed class FlagOwner
    {
        public object Neighbor;
        public FlagChild Child;
        public int Marker;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetNestedFlag(Payload unused)
        {
            Child.Flag = true;
        }
    }
}
