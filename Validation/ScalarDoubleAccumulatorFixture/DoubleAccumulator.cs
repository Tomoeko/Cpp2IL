using System.Runtime.CompilerServices;

namespace ScalarDoubleAccumulatorFixture
{
    public class DoubleAccumulator
    {
        public bool Pending;
        public double Current;
        public double Baseline;
        public double Total;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Apply()
        {
            if (Pending)
            {
                double delta = Current - Baseline;
                Pending = false;
                Total = Total + delta;
            }
        }
    }
}
