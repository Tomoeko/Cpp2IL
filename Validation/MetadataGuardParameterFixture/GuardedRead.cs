using System.Runtime.CompilerServices;

namespace MetadataGuardParameterFixture
{
    public static class SharedState
    {
        public static int Value;
        public static int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int AddParameter(int parameter)
        {
            return unchecked(Value + parameter);
        }
    }
}
