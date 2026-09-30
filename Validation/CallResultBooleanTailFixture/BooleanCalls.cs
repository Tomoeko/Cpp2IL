using System.Runtime.CompilerServices;

namespace CallResultBooleanTailFixture
{
    public class TailNode
    {
        public bool Value;
        public int ApplyCount;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Apply(bool value)
        {
            Value = value;
            ApplyCount = unchecked(ApplyCount + 1);
        }
    }

    public class TailOwner
    {
        public TailNode Node;
        public int ProducerCount;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public TailNode GetNode()
        {
            ProducerCount = unchecked(ProducerCount + 1);
            return Node;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ForwardFalse() { GetNode().Apply(false); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ForwardTrue() { GetNode().Apply(true); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ForwardParameter(bool value) { GetNode().Apply(value); }
    }

    public static class StaticCalls
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ForwardFalse(TailOwner owner) { owner.GetNode().Apply(false); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ForwardTrue(TailOwner owner) { owner.GetNode().Apply(true); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ForwardParameter(TailOwner owner, bool value) { owner.GetNode().Apply(value); }
    }
}
