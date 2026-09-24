using System.Runtime.CompilerServices;

namespace ConstructorThunkChainFixture
{
    public class PayloadBase
    {
        public int State;
        public object Neighbor;
        public object Untouched;
    }

    public class FirstRoot : PayloadBase
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public FirstRoot()
        {
            State = 29;
        }
    }

    public class FirstMiddle : FirstRoot
    {
    }

    public sealed class FirstLeaf : FirstMiddle
    {
    }

    public class SecondRoot : PayloadBase
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public SecondRoot()
        {
            State = 29;
        }
    }

    public class SecondMiddle : SecondRoot
    {
    }

    public sealed class SecondLeaf : SecondMiddle
    {
    }
}
