using System.Runtime.CompilerServices;

namespace LoopCallFixture
{
    public sealed class LoopState
    {
        public int Value;
        public int Calls;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Step(int delta)
        {
            Value = unchecked(Value + delta);
            Calls = unchecked(Calls + 1);
            return Value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Run(LoopState state, int count, int seed)
        {
            var total = seed;
            for (var index = 0; index < count; index++)
                total = unchecked(total + state.Step(index));
            return total;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int RunUntil(LoopState state, int count, int stop)
        {
            var total = 0;
            for (var index = 0; index < count; index++)
            {
                var value = state.Step(index);
                total = unchecked(total + value);
                if (value == stop)
                    break;
            }
            return total;
        }
    }
}
