using System.Runtime.CompilerServices;

namespace NativeSubnormalFieldStoreFixture
{
    public class InvocationNode
    {
        public int Calls;
        public bool Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Set(bool value) { Calls = unchecked(Calls + 1); Value = value; }
    }

    public class InvocationHolder
    {
        public InvocationNode Target;
        public float SingleMarker;
        public double DoubleMarker;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StoreSingleMinimum(bool value) { Target.Set(value); SingleMarker = 1.40129846e-45F; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StoreSingleMaximum(bool value) { Target.Set(value); SingleMarker = 1.17549421e-38F; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StoreSingleNegativeMinimum(bool value) { Target.Set(value); SingleMarker = -1.40129846e-45F; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StoreSingleNegativeMaximum(bool value) { Target.Set(value); SingleMarker = -1.17549421e-38F; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StoreDoubleMinimum(bool value) { Target.Set(value); DoubleMarker = 4.9406564584124654e-324D; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StoreDoubleMaximumAndSingleMinimum(bool value)
        {
            Target.Set(value);
            DoubleMarker = 2.2250738585072009e-308D;
            SingleMarker = -1.40129846e-45F;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StoreDoubleNegativeMinimum(bool value) { Target.Set(value); DoubleMarker = -4.9406564584124654e-324D; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StoreDoubleNegativeMaximum(bool value) { Target.Set(value); DoubleMarker = -2.2250738585072009e-308D; }
    }
}
