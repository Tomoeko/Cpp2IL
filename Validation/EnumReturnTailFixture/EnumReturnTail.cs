using System.Runtime.CompilerServices;

namespace EnumReturnTailFixture
{
    public enum Choice : int
    {
        Negative = -1,
        Zero = 0,
        One = 1,
        Maximum = int.MaxValue
    }

    public sealed class ChoiceReader
    {
        public Choice Current;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Choice ReadChoice()
        {
            return Current;
        }
    }

    public sealed class ChoiceOwner
    {
        public long Pad00, Pad01, Pad02, Pad03, Pad04, Pad05;
        public long Pad06, Pad07, Pad08, Pad09, Pad10, Pad11;
        public long Pad12, Pad13, Pad14, Pad15, Pad16, Pad17;
        public ChoiceReader Receiver;

        public Choice CurrentChoice
        {
            get { return Receiver.ReadChoice(); }
        }
    }
}
