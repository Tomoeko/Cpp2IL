using System.Runtime.CompilerServices;

namespace NativeDerivedReceiverInvocationFixture
{
    public class BaseNode
    {
        public int Calls;
        public int Value;
        public bool Flag;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetInt(int value) { Calls = unchecked(Calls + 1); Value = value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetFlag(bool flag) { Calls = unchecked(Calls + 1); Flag = flag; }
    }

    public class DerivedNode : BaseNode
    {
        public int Extra;
    }

    public class InvocationHolder
    {
        public DerivedNode Target;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ForwardInt(int value) { Target.SetInt(value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ForwardFlag(bool flag) { Target.SetFlag(flag); }
    }
}
