using System.Runtime.CompilerServices;

namespace NativeScalarDoubleLeafFixture
{
    public class DoubleFields
    {
        public double First;
        public double Second;
        public static readonly int Marker = 17;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public double Sum()
        {
            return First + Second;
        }
    }

    public static class DoubleConversions
    {
        public static readonly int Marker = 29;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static double Eighth(long value)
        {
            return (double)value * 0.125;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static double EighthAlias(long value)
        {
            return (double)value * 0.125;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static double Tenth(long value)
        {
            return (double)value * 0.1;
        }
    }
}
