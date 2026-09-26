using System.Runtime.CompilerServices;

namespace NestedSingleGetterFixture
{
    public static class CellContainer
    {
        public sealed class Cell
        {
            public long Before;
            public float Level;
            public long After;
        }
    }

    public sealed class FloatReader
    {
        public long Before;
        public CellContainer.Cell Child;
        public long After;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public float ReadLevel()
        {
            return Child.Level;
        }
    }
}
