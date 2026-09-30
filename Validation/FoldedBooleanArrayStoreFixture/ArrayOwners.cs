using System.Runtime.CompilerServices;

namespace FoldedBooleanArrayStoreFixture
{
    public class FieldlessBase<T>
    {
    }

    public sealed class FirstArrayOwner : FieldlessBase<int>
    {
        public long Prefix0;
        public long Prefix1;
        public bool[] Values;
        public long Suffix;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetFalse(int index)
        {
            Values[index] = false;
        }
    }

    public sealed class SecondArrayOwner : FieldlessBase<int>
    {
        public long Prefix0;
        public long Prefix1;
        public bool[] Values;
        public long Suffix;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetFalse(int index)
        {
            Values[index] = false;
        }
    }
}
