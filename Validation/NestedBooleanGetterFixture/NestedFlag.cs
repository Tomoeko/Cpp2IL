using System.Runtime.CompilerServices;

namespace NestedBooleanGetterFixture
{
    public sealed class FlagCell
    {
        public int Before;
        public bool Flag;
        public object After;
        public int Count;
    }

    public sealed class FlagHolder
    {
        public object Before;
        public FlagCell Child;
        public int After;

        public bool NestedFlag
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return Child.Flag; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadCount()
        {
            return Child.Count;
        }
    }
}
