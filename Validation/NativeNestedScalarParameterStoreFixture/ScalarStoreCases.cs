using System.Runtime.CompilerServices;

namespace NativeNestedScalarParameterStoreFixture
{
    public sealed class Cell
    {
        public byte Before;
        public bool Enabled;
        public int Signed;
        public uint Unsigned;
        public float Amount;
        public byte After;
    }

    public sealed class Holder
    {
        public Cell Target;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StoreBoolean(bool value) { Target.Enabled = value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StoreInt32(int value) { Target.Signed = value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StoreUInt32(uint value) { Target.Unsigned = value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StoreSingle(float value) { Target.Amount = value; }
    }
}
