using System.Runtime.CompilerServices;

namespace FinalInterfaceBooleanGetterFixture
{
    public interface IFlag
    {
        bool Value { get; }
        bool Read();
    }

    public class FlagState : IFlag
    {
        public bool Flag;
        public int Neighbor;
        public object Reference;

        public bool Value
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Flag; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool Read()
        {
            return Flag;
        }
    }

    public class ExplicitFlagState : IFlag
    {
        public bool Flag;
        public int Neighbor;
        public object Reference;

        bool IFlag.Value
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Flag; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        bool IFlag.Read()
        {
            return Flag;
        }
    }

    public class ShadowFlagState : FlagState
    {
        public bool ShadowFlag;
        public int ShadowNeighbor;
        public object ShadowReference;

        public new bool Value
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return ShadowFlag; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public new bool Read()
        {
            return ShadowFlag;
        }
    }
}
