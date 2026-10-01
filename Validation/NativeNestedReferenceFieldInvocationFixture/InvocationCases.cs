using System.Runtime.CompilerServices;

namespace NativeNestedReferenceFieldInvocationFixture
{
    public class Payload
    {
    }

    public class SourceOwner
    {
        public Payload Payload;
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
        public SourceOwner Source;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Forward()
        {
            Target.Accept(Source.Payload);
        }
    }
}
