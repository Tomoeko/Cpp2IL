using System.Runtime.CompilerServices;

namespace RecoveryFixture
{
    public sealed class RecoveryLogic
    {
        public int Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Add(int left, int right)
        {
            return unchecked(left + right);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Select(int value, int threshold)
        {
            return value < threshold ? unchecked(threshold - value) : unchecked(value + threshold);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Accumulate(int delta)
        {
            Value = Add(Value, delta);
            return Value;
        }
    }
}
