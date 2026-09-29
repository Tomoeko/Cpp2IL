using System.Runtime.CompilerServices;

namespace FinalOverrideBooleanGetterFixture
{
    public abstract class FlagBase
    {
        public object BaseReference;
        public int BaseNeighbor;

        public abstract bool Value { get; }
    }

    public class FlagState : FlagBase
    {
        public bool Flag;
        public int Neighbor;
        public object Reference;

        public sealed override bool Value
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Flag; }
        }
    }

    public sealed class ShadowState : FlagState
    {
        public bool ShadowFlag;
        public int ShadowNeighbor;

        public new bool Value
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return ShadowFlag; }
        }
    }
}
