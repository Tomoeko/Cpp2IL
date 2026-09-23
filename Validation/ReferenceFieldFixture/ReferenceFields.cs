using System.Runtime.CompilerServices;

namespace ReferenceFieldFixture
{
    public class ReferenceBox
    {
        public string Text;
    }

    public sealed class DerivedBox : ReferenceBox
    {
        public int Marker;
    }

    public class ReferenceOuter
    {
        public ReferenceBox Inner;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string ReadInner()
        {
            return Inner.Text;
        }
    }

    public sealed class DerivedOuter : ReferenceOuter
    {
        public long Marker;
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
