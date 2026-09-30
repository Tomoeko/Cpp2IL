using System.Runtime.CompilerServices;

namespace NativeScalarProducerInvocationFixture
{
    public class Node
    {
        public int Reads;
        public int Calls;
        public int Value;
        public InvocationHolder Owner;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadValue()
        {
            Reads = unchecked(Reads + 1);
            Owner.ReplaceFields();
            return Value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetValue(int value)
        {
            Calls = unchecked(Calls + 1);
            Value = value;
        }
    }

    public class InvocationHolder
    {
        public Node Source;
        public Node Target;
        public Node Replacement;
        public int Value;
        public int Neighbor;
        public bool Flag;
        public int BeforeCount;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ReplaceFields()
        {
            Target = Replacement;
            Value = Neighbor;
            Flag = !Flag;
            BeforeCount = unchecked(BeforeCount + 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Forward() { Target.SetValue(Source.ReadValue()); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ForwardSnapshot()
        {
            var target = Target;
            var value = Source.ReadValue();
            target.SetValue(value);
        }
    }
}
