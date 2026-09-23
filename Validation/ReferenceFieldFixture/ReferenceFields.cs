using System.Runtime.CompilerServices;

namespace ReferenceFieldFixture
{
    public class ReferenceBox
    {
        public int[] Prefix;
        public int[] Prefix2;
        public int[] Prefix3;
        public int[] Prefix4;
        public int[] Prefix5;
        public string Text;
        public int[] Suffix;
    }

    public sealed class DerivedBox : ReferenceBox
    {
        public int Marker;
    }

    public class ReferenceOuter
    {
        public int[] Prefix;
        public int[] Prefix2;
        public int[] Prefix3;
        public int[] Prefix4;
        public int[] Prefix5;
        public ReferenceBox Inner;
        public int[] Suffix;

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

    public class SharedBox
    {
        public int[] Prefix;
        public string Text;
        public int[] Suffix;
    }

    public class SharedOuter
    {
        public int[] Prefix;
        public SharedBox Inner;
        public int[] Suffix;

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

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string ReadShared(SharedBox box)
        {
            return box.Text;
        }
    }
}
