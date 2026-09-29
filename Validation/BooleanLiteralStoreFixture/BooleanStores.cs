using System.Runtime.CompilerServices;

namespace BooleanLiteralStoreFixture
{
    public sealed class BooleanStoreTarget
    {
        public bool State;
        public bool Neighbor;
    }

    public sealed class BooleanStoreOwner
    {
        public int Marker;
        public BooleanStoreTarget Current;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public BooleanStoreTarget Acquire()
        {
            Marker++;
            return Current;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Enable()
        {
            var target = Acquire();
            target.State = true;
            Marker += 4;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Disable()
        {
            var target = Acquire();
            target.State = false;
            Marker += 8;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void EnableAfterMutation(int increment)
        {
            Marker += increment;
            var target = Acquire();
            target.State = true;
            Marker += 2;
        }
    }
}
