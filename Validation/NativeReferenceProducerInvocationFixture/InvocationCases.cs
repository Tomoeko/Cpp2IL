using System.Runtime.CompilerServices;

namespace NativeReferenceProducerInvocationFixture
{
    public class Node
    {
        public int Calls;
        public bool Flag;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetFlag(bool value) { Calls = unchecked(Calls + 1); Flag = value; }
    }

    public class Provider
    {
        public int Reads;
        public Node Result;
        public InvocationHolder Owner;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Node ReadTarget()
        {
            Reads = unchecked(Reads + 1);
            Owner.ReplaceFields();
            return Result;
        }
    }

    public class InvocationHolder
    {
        public Provider Source;
        public Provider Replacement;
        public int Value;
        public int Neighbor;
        public int BeforeCount;
        public bool Flag;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ReplaceFields()
        {
            Source = Replacement;
            Value = Neighbor;
            Flag = !Flag;
            BeforeCount = unchecked(BeforeCount + 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Forward(bool value) { Source.ReadTarget().SetFlag(value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ForwardSnapshot(bool value)
        {
            var source = Source;
            var target = source.ReadTarget();
            target.SetFlag(value);
        }
    }
}
