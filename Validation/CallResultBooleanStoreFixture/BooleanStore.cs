using System.Runtime.CompilerServices;

namespace CallResultBooleanStoreFixture
{
    public class FlagCell
    {
        public bool Enabled;
        public int Neighbor;
    }

    public class FlagOwner
    {
        public int TouchCount;
        public FlagCell Cell;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Touch()
        {
            TouchCount = 1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public FlagCell GetCell()
        {
            return Cell;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Enable()
        {
            Touch();
            GetCell().Enabled = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Disable()
        {
            Touch();
            GetCell().Enabled = false;
        }
    }
}
