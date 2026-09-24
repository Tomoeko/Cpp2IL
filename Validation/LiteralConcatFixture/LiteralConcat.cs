using System.Runtime.CompilerServices;

namespace LiteralConcatFixture
{
    public class TextNode
    {
        public string Text;
        public int Marker;
    }

    public sealed class DerivedTextNode : TextNode
    {
        public int DerivedMarker;
    }

    public class ResolverBase
    {
        public DerivedTextNode Current;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        protected DerivedTextNode Lookup()
        {
            return Current;
        }
    }

    public class Resolver : ResolverBase
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual string Compose()
        {
            return Lookup().Text + "|suffix";
        }
    }

    public sealed class FurtherResolver : Resolver
    {
        public int Extra;
    }
}
