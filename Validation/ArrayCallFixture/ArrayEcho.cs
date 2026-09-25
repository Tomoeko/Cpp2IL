using System.Runtime.CompilerServices;

namespace ArrayCallFixture
{
    public class ArrayEcho
    {
        public int Calls;
        public int[] ReturnedValues;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int[] Echo(int[] values)
        {
            Calls = unchecked(Calls + 1);
            return values;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int[] ReturnConfigured(int[] values)
        {
            Calls = unchecked(Calls + 1);
            return ReturnedValues;
        }
    }

    public sealed class DerivedEcho : ArrayEcho
    {
    }

    public sealed class FieldForwarder
    {
        public ArrayEcho Receiver;
        public int[] Values;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int[] ForwardField()
        {
            return Receiver.Echo(Values);
        }
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int[] ForwardTwo(ArrayEcho first, ArrayEcho second, int[] values)
        {
            first.Echo(values);
            return second.Echo(values);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReadFromEcho(ArrayEcho echo, int[] values, int index)
        {
            int[] returned = echo.ReturnConfigured(values);
            return returned[index];
        }
    }
}
