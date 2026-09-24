using System;
using System.Runtime.CompilerServices;

namespace BooleanGetterMetadataFixture
{
    public class ReferenceRoot
    {
        public IntPtr InheritedWord;
    }

    public class EmptyLayerOne : ReferenceRoot
    {
    }

    public class EmptyLayerTwo : EmptyLayerOne
    {
    }

    public class EmptyLayerThree : EmptyLayerTwo
    {
    }

    public class GenericAncestor<T> : EmptyLayerThree
    {
    }

    public sealed class GenericFieldOwner : GenericAncestor<object>
    {
        public bool Value;
        public object NeighborReference;

        public bool ReadValue
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Value; }
        }
    }

    public class VirtualBooleanBase
    {
        public bool BaseValue;

        public virtual bool ReadValue
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return BaseValue; }
        }
    }

    public sealed class VirtualBooleanOverride : VirtualBooleanBase
    {
        public bool OverrideValue;

        public override bool ReadValue
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return OverrideValue; }
        }
    }
}
