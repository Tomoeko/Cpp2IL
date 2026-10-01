using System.Runtime.CompilerServices;

namespace NativeNestedReferenceGetterFoldedInvocationFixture
{
    public class Payload
    {
    }

    public class SourceOwner
    {
        protected Payload _payload;

        public Payload Payload
        {
            get { return _payload; }
        }
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

    public class MirrorSource
    {
        protected Payload _payload;

        public Payload Payload
        {
            get { return _payload; }
        }
    }
}
