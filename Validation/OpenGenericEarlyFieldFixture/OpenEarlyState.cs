using System.Runtime.CompilerServices;

namespace OpenGenericEarlyFieldFixture
{
    public sealed class OpenEarlyState<T>
    {
        public int First;
        public object Anchor;
        public bool Flag;
        public T Later;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadFirst()
        {
            return First;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public object ReadAnchor()
        {
            return Anchor;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadFlag()
        {
            return Flag;
        }
    }
}
