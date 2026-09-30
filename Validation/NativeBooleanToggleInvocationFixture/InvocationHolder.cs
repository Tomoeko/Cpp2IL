using System.Runtime.CompilerServices;

namespace NativeBooleanToggleInvocationFixture
{
    public class Node
    {
        public int Calls;
        public bool Flag;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetFlag(bool flag)
        {
            Calls = unchecked(Calls + 1);
            Flag = flag;
        }
    }

    public class InvocationHolder
    {
        public bool Flag;
        public Node Target;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ToggleAndForward()
        {
            var flag = !Flag;
            Flag = flag;
            Target.SetFlag(flag);
        }
    }
}
