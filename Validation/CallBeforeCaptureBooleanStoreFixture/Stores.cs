using System.Runtime.CompilerServices;

namespace CallBeforeCaptureBooleanStoreFixture
{
    public sealed class StoreTarget
    {
        public bool Flag;
        public bool Neighbor;
        public int Sentinel;
    }

    public class StoreBase
    {
        protected StoreTarget Current;
    }

    public class StoreOwner : StoreBase
    {
        public StoreTarget Other;
        public int Counter;
        public int Sentinel;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Touch()
        {
            unchecked { Counter++; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetAfterTouch()
        {
            Touch();
            var captured = Current;
            captured.Flag = true;
        }
    }
}
