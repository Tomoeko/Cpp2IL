using System.Runtime.CompilerServices;

namespace FieldBooleanArrayReadFixture
{
    public sealed class BooleanArrayOwner
    {
        public int Before;
        public bool[] First;
        public int Between;
        public bool[] Second;
        public long Pad0;
        public long Pad1;
        public long Pad2;
        public long Pad3;
        public long Pad4;
        public long Pad5;
        public long Pad6;
        public bool[] Late;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadFirst(int index)
        {
            return First[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadSecond(int index)
        {
            return Second[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadLate(int index)
        {
            return Late[index];
        }
    }
}
