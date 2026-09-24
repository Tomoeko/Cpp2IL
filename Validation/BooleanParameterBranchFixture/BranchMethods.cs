using System.Runtime.CompilerServices;

namespace BooleanParameterBranchFixture
{
    public static class BranchMethods
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Select(int unusedValue, bool useHighPath)
        {
            if (useHighPath)
                return HighPath();

            return LowPath();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int HighPath()
        {
            return 17;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int LowPath()
        {
            return -17;
        }
    }
}
