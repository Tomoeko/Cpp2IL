using System.Runtime.CompilerServices;

namespace NestedLiteralStoreFixture
{
    public sealed class StoreTarget
    {
        public bool Enabled;
        public int Signed;
        public uint Unsigned;
        public int Neighbor;
    }

    public sealed class StoreOwner
    {
        public StoreTarget First;
        public StoreTarget Second;
        public StoreTarget Replacement;
        public int Marker;
        public bool Prior;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Touch(int increment)
        {
            Marker += increment;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ReplaceFirst()
        {
            Marker++;
            First = Replacement;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void BooleanPair(bool prior)
        {
            var first = First;
            Prior = prior;
            first.Enabled = true;
            var second = Second;
            second.Enabled = false;
            Marker += 1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SignedPair(int increment)
        {
            var first = First;
            Touch(increment);
            first.Signed = -1;
            var second = Second;
            second.Signed = int.MinValue;
            Marker += 2;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void UnsignedPair()
        {
            var first = First;
            first.Unsigned = 0x80000000u;
            var second = Second;
            second.Unsigned = uint.MaxValue;
            Marker += 3;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void CapturedReplacement()
        {
            var captured = First;
            ReplaceFirst();
            captured.Enabled = true;
            Second.Enabled = false;
            Marker += 4;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SetParameter(StoreOwner owner)
        {
            var first = owner.First;
            first.Enabled = false;
            first.Signed = int.MaxValue;
            first.Unsigned = uint.MaxValue;
        }
    }
}
