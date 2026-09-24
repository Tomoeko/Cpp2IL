using System.Runtime.CompilerServices;

namespace InstanceReferencePropertyFixture
{
    public class ReferenceCell
    {
        public object Neighbor;
        public object Stored;
        public int Marker;

        public object Value
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Stored; }

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
