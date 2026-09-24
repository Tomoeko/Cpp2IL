using System.Runtime.CompilerServices;

namespace StaticScalarSetterFixture
{
    public static class StaticState
    {
        public static int Before;
        public static int Value;
        public static int After;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Assign(int value)
        {
            Value = value;
        }
    }
}
