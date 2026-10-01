using System.Runtime.CompilerServices;

namespace NativeEnumArgumentInvocationFixture
{
    public enum Selector : int
    {
        Zero = 0,
        Positive = 17,
        Negative = -7
    }

    public class Sink
    {
        public Selector Last;
        public int Calls;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Accept(Selector value)
        {
            Calls = unchecked(Calls + 1);
            Last = value;
        }
    }

    public static class Callers
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Zero(Sink target) { target.Accept(Selector.Zero); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Positive(Sink target) { target.Accept(Selector.Positive); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Negative(Sink target) { target.Accept(Selector.Negative); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Unnamed(Sink target) { target.Accept((Selector)1234567); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Forward(Sink target, Selector value) { target.Accept(value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Sequence(Sink first, Sink second, Selector value)
        {
            first.Accept(value);
            second.Accept(Selector.Negative);
            first.Accept((Selector)1234567);
        }
    }
}
