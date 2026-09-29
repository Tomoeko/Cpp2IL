using System.Runtime.CompilerServices;

namespace WideReferenceNullFixture
{
    public sealed class ReferenceSlotOwner
    {
        public object Pad00;
        public object Pad01;
        public object Pad02;
        public object Pad03;
        public object Pad04;
        public object Pad05;
        public object Pad06;
        public object First;
        public object Gap;
        public object Second;

        public bool HasNear
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Pad01 != null; }
        }

        public bool HasFirst
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return First != null; }
        }

        public bool IsSecondNull
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Second == null; }
        }
    }
}
