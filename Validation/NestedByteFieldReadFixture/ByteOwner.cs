using System.Runtime.CompilerServices;

namespace NestedByteFieldReadFixture
{
    public sealed class ByteCell
    {
        public byte Value;
        public int Neighbor;
        public object Reference;
    }

    public sealed class ByteOwner
    {
        public ByteCell Child;
        public object Reference;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadUnsigned()
        {
            return Child.Value;
        }
    }
}
