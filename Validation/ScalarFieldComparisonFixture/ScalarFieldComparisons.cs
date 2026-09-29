using System.Runtime.CompilerServices;

namespace ScalarFieldComparisonFixture
{
    public sealed class ScalarFieldState
    {
        public float Single;
        public double Double;
        public int Updates;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void RetainGreater32(float value)
        {
            if (value > Single)
            {
                Single = value;
                Updates++;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void RetainGreater64(double value)
        {
            if (value > Double)
            {
                Double = value;
                Updates++;
            }
        }
    }
}
