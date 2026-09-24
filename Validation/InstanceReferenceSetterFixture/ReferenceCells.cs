using System.Runtime.CompilerServices;

namespace InstanceReferenceSetterFixture
{
    public class ReferenceCell
    {
        public object Neighbor;
        public object Stored;
        public int Marker;

        public object Value
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            set { Stored = value; }
        }
    }

    public class TextCell
    {
        public object Neighbor;
        public string Stored;
        public int Marker;

        public string Value
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            set { Stored = value; }
        }
    }
}
