using System.Runtime.CompilerServices;

namespace ScalarWrapperFixture
{
    public struct UnsignedCellA
    {
        public uint Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override string ToString()
        {
            return Value.ToString();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int CompareTo(UnsignedCellA other)
        {
            return Value.CompareTo(other.Value);
        }
    }

    public struct UnsignedCellB
    {
        public uint Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override string ToString()
        {
            return Value.ToString();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int CompareTo(UnsignedCellB other)
        {
            return Value.CompareTo(other.Value);
        }
    }

    public struct WideUnsignedCell
    {
        public ulong Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override string ToString()
        {
            return Value.ToString();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override int GetHashCode()
        {
            return Value.GetHashCode();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int CompareTo(WideUnsignedCell other)
        {
            return Value.CompareTo(other.Value);
        }
    }
}
