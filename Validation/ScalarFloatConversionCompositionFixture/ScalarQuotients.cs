using System.Runtime.CompilerServices;

namespace ScalarFloatConversionCompositionFixture
{
    public struct IgnoredOptions { }

    public class ScalarQuotients
    {
        public double Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ScalarQuotients() { }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float NarrowDivide(int first, int second, float divisor, double value)
        {
            return (float)value / divisor;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static float NegativeToPositive(int first, int second, float divisor, double value)
        {
            var quotient = (float)value / divisor;
            return quotient < 0 ? -quotient : quotient;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public float InstanceDivide(int unused, float divisor, double value)
        {
            return (float)value / divisor;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public float InstanceNegativeToPositive(int unused, float divisor, double value)
        {
            var quotient = (float)value / divisor;
            return quotient < 0 ? -quotient : quotient;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public float FieldDivide(int unused, float divisor)
        {
            return (float)Value / divisor;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public float FieldNegativeToPositive(int unused, float divisor)
        {
            var quotient = (float)Value / divisor;
            return quotient < 0 ? -quotient : quotient;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual float AggregateNegativeToPositive(IgnoredOptions options, float divisor, double value)
        {
            var quotient = (float)value / divisor;
            return quotient < 0 ? -quotient : quotient;
        }
    }
}
