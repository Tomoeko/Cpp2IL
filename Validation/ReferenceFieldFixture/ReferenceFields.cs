using System.Runtime.CompilerServices;

namespace ReferenceFieldFixture
{
    public sealed class ReferenceBox
    {
        public string Text;
    }

    public sealed class ReferenceOuter
    {
        public ReferenceBox Inner;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string ReadInner()
        {
            return Inner.Text;
        }
    }

    public static class ReferenceReads
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Read(ReferenceBox box)
        {
            return box.Text;
        }
    }
}
