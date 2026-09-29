using System.Runtime.CompilerServices;

namespace DynamicVirtualDispatchFixture
{
    public class BaseState
    {
        public int Counter;
        public int Neighbor;
        public object Reference;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual int Apply(int input)
        {
            Counter = unchecked(Counter + 1);
            return unchecked(input + 3);
        }
    }

    public class OverrideState : BaseState
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override int Apply(int input)
        {
            Counter = unchecked(Counter + 2);
            return unchecked(input - 5);
        }
    }

    public sealed class InheritedState : OverrideState
    {
    }

    public sealed class ShadowState : OverrideState
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public new int Apply(int input)
        {
            Counter = unchecked(Counter + 8);
            return unchecked(input + 11);
        }
    }

    public sealed class Marker
    {
        public int Value;
        public int Neighbor;
        public object Reference;
    }

    public static class Dispatcher
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int CallVirtual(BaseState receiver, int input)
        {
            return receiver.Apply(input);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int CallVirtualThenStore(BaseState receiver, int input, Marker marker)
        {
            var result = receiver.Apply(input);
            marker.Value = result;
            return result;
        }
    }
}
