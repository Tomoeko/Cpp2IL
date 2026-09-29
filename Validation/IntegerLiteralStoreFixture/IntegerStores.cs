using System.Runtime.CompilerServices;

namespace IntegerLiteralStoreFixture
{
    public sealed class IntegerStoreTarget
    {
        public int Signed;
        public uint Unsigned;
        public int Neighbor;
    }

    public sealed class IntegerStoreOwner
    {
        public int Marker;
        public IntegerStoreTarget Current;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public IntegerStoreTarget Acquire()
        {
            Marker++;
            return Current;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Touch(int increment)
        {
            Marker += increment;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SignedNegative()
        {
            var target = Acquire();
            target.Signed = -1;
            Marker += 1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SignedMinimum(int increment)
        {
            var target = Acquire();
            Touch(increment);
            target.Signed = int.MinValue;
            Marker += 2;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SignedMaximum()
        {
            var target = Acquire();
            target.Signed = int.MaxValue;
            Marker += 3;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UnsignedHigh()
        {
            var target = Acquire();
            target.Unsigned = 0x80000000u;
            Marker += 4;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UnsignedMaximum()
        {
            var target = Acquire();
            target.Unsigned = uint.MaxValue;
            Marker += 5;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SetParameter(IntegerStoreTarget target)
        {
            target.Signed = 1;
        }
    }
}
