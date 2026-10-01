using System.Runtime.CompilerServices;

namespace NativeNestedOwnerScalarParameterStoreFixture
{
    public sealed class Cell
    {
        public byte Before;
        public float Amount;
        public byte After;
    }

    public static class Container
    {
        public sealed class Holder
        {
            public Cell Target;

            [MethodImpl(MethodImplOptions.NoInlining)]
            public void StoreSingle(float value) { Target.Amount = value; }
        }
    }
}
