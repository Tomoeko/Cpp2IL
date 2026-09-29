using System.Runtime.CompilerServices;

namespace BooleanCompositionFixture
{
    public sealed class CompositionState
    {
        public float Single;
        public double Double;
        public int Updates;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void RetainGreater32(float value, float threshold)
        {
            if (value > threshold)
            {
                Single = value;
                Updates++;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void RetainGreater64(double value, double threshold)
        {
            if (value > threshold)
            {
                Double = value;
                Updates++;
            }
        }
    }
}
