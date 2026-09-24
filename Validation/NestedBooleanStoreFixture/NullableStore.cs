using System.Runtime.CompilerServices;

namespace NestedBooleanStoreFixture
{
    public sealed class BooleanTarget
    {
        public long Neighbor;
        public bool Flag;
    }

    public sealed class BooleanOwner
    {
        // Keep the ordinary reference field beyond an x64 signed byte displacement.
        // Every field is observed by the validation harness so stripping preserves it.
        public long Pad00, Pad01, Pad02, Pad03, Pad04;
        public long Pad05, Pad06, Pad07, Pad08, Pad09;
        public long Pad10, Pad11, Pad12, Pad13, Pad14;
        public BooleanTarget Target;
        public long Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ClearFlag()
        {
            Target.Flag = false;
        }
    }
}
