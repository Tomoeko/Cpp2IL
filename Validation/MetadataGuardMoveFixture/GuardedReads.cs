using System.Runtime.CompilerServices;

namespace MetadataGuardMoveFixture
{
    public sealed class ValueBox
    {
        public int Value;
    }

    public static class SharedState
    {
        public static int Value;
        public static int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int AddParameter(int parameter)
        {
            return unchecked(Value + parameter);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int AddBox(ValueBox box)
        {
            return unchecked(Value + box.Value);
        }
    }
}
