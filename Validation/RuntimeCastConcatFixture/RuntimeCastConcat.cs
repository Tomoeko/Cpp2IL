using System;
using System.Runtime.CompilerServices;

namespace RuntimeCastConcatFixture
{
    public class BaseNode
    {
        public string Text;
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

    public interface INodeOwner
    {
        BaseNode Current { get; set; }
    }

    public class PropertyResolverBase : INodeOwner
    {
        public BaseNode Current { get; set; }
        public int Neighbor;
    }

    public class PropertyResolverTwin : INodeOwner
    {
        public BaseNode Current { get; set; }
        public int Neighbor;
    }

    public class PropertyResolver : PropertyResolverBase
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public DerivedNode Lookup()
        {
            return Current as DerivedNode;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual string Compose()
        {
            return Lookup().Text + "|tag";
        }
    }

    public static class MetadataControls
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string AppendLiteral(string value)
        {
            return value + "|tag";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Type TargetType()
        {
            return typeof(DerivedNode);
        }
    }

    public class ResolverBase
    {
        public BaseNode Current;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public DerivedNode Lookup()
        {
            return Current as DerivedNode;
        }
    }

    public class Resolver : ResolverBase
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual string Compose()
        {
            return Lookup().Text + "|tag";
        }
    }

    public sealed class FurtherResolver : Resolver
    {
        public int Extra;
    }
}
