using System.Runtime.CompilerServices;

namespace NativeReferenceFieldInvocationFixture
{
    public class Payload
    {
    }

    public class Node
    {
        public int Calls;
        public Payload Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Accept(Payload incoming)
        {
            Calls = unchecked(Calls + 1);
            Value = incoming;
        }
    }

    public class InvocationHolder
    {
        public Node Target;
        public Payload Source;
        public bool Flag;
        public float Marker;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Forward()
        {
            var target = Target;
            Flag = true;
            Marker = 0.0f;
            target.Accept(Source);
        }
    }
}
