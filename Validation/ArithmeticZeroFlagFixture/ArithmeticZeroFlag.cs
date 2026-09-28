using System.Runtime.CompilerServices;

namespace ArithmeticZeroFlagFixture
{
    public sealed class ArithmeticZeroFlag
    {
        public int Last32;
        public long Last64;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool SubtractAndTest(int left, int right)
        {
            var result = unchecked(left - right);
            Last32 = result;
            return result == 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool AddAndTest(int left, int right)
        {
            var result = unchecked(left + right);
            Last32 = result;
            return result == 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool SubtractWideAndTest(long left, long right)
        {
            var result = unchecked(left - right);
            Last64 = result;
            return result == 0;
        }
    }
}
