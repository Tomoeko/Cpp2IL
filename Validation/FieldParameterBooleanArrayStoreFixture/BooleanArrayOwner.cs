using System.Runtime.CompilerServices;

namespace FieldParameterBooleanArrayStoreFixture
{
    public sealed class BooleanArrayOwner
    {
        public long Prefix0;
        public long Prefix1;
        public long Prefix2;
        public bool[] Values;
        public bool[] OtherValues;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetAt(int index, bool value)
        {
            Values[index] = value;
        }
    }
}
