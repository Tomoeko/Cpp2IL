using System.Runtime.CompilerServices;

namespace ClassCastLookupFixture
{
    public class BaseNode
    {
        public string Label;
        public int Marker;
    }

    public class DerivedNode : BaseNode
    {
        public int Detail;
    }

    public sealed class FurtherNode : DerivedNode
    {
        public int Extra;
    }

    public class Resolver
    {
        public BaseNode Current;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public DerivedNode Lookup()
        {
            return Current as DerivedNode;
        }
    }
}
