using System.Runtime.CompilerServices;

namespace ArrayCallFixture
{
    public sealed class ArrayEcho
    {
        public int Calls;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int[] Echo(int[] values)
        {
            Calls = unchecked(Calls + 1);
            return values;
        }
    }

    public static class ArrayCalls
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int[] Forward(ArrayEcho echo, int[] values)
        {
            return echo.Echo(values);
        }
    }
}
