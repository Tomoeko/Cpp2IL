using System.Runtime.CompilerServices;

namespace WideFieldLow32Fixture
{
    public sealed class SignedState
    {
        public long Value;
        public int Neighbor;
        public object Reference;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadSigned() { return unchecked((int)Value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public uint ReadUnsigned() { return unchecked((uint)Value); }
    }

    public sealed class UnsignedState
    {
        public ulong Value;
        public int Neighbor;
        public object Reference;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadSigned() { return unchecked((int)Value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public uint ReadUnsigned() { return unchecked((uint)Value); }
    }
}
