using System.Runtime.CompilerServices;

namespace ConstructedBaseBooleanArrayFixture
{
    public class FieldlessGenericBase<T>
    {
    }

    public sealed class GenericBooleanArrayState : FieldlessGenericBase<int>
    {
        public long Before;
        public bool[] Values;
        public long After;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetTrue(int index)
        {
            Values[index] = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetFalse(int index)
        {
            Values[index] = false;
        }
    }
}
