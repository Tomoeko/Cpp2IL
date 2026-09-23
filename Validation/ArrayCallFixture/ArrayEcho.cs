using System.Runtime.CompilerServices;

namespace ArrayCallFixture
{
    public class ArrayEcho
    {
        public int Calls;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int[] Echo(int[] values)
        {
            Calls = unchecked(Calls + 1);
            return values;
        }
    }

    public sealed class DerivedEcho : ArrayEcho
    {
    }

    public static class ArrayCalls
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int[] Forward(ArrayEcho echo, int[] values)
        {
            return echo.Echo(values);
        }


        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int[] ForwardDerived(DerivedEcho echo, int[] values)
        {
            return echo.Echo(values);
        }
    }
}
