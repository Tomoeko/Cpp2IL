using System.Runtime.CompilerServices;

namespace ScalarWrapperCctorFixture
{
    public struct NarrowCell
    {
        public uint Value;
        public static readonly NarrowCell Seed = new NarrowCell { Value = 17u };

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override string ToString()
        {
            return Value.ToString();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int CompareTo(NarrowCell other)
        {
            return Value.CompareTo(other.Value);
        }
    }

    public struct WideCell
    {
        public ulong Value;
        public static readonly WideCell Seed = new WideCell
        {
            Value = 0xffffffff80000000ul
        };

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
        public int CompareTo(WideCell other)
        {
            return Value.CompareTo(other.Value);
        }
    }

}
