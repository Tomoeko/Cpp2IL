using System.Runtime.CompilerServices;

namespace NativeDirectGenericReferenceInvocationFixture
{
    public class First
    {
    }

    public class Second
    {
    }

    public class Relay
    {
        public int Calls;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public T Echo<T>(T value) where T : class
        {
            Calls = unchecked(Calls + 1);
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public First FirstCall(First value)
        {
            return Echo<First>(value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Second SecondCall(Second value)
        {
            return Echo<Second>(value);
        }
    }
}
