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
        public object Payload;
        public int[] Numbers;
        public string[] Labels;
        public object[] Objects;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string ReadTextSelf()
        {
            return Text;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public object ReadObjectSelf()
        {
            return Payload;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int[] ReadArraySelf()
        {
            return Numbers;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string[] ReadLabelsSelf()
        {
            return Labels;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public object[] ReadObjectsSelf()
        {
            return Objects;
        }
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        public object ReadObjectInner()
        {
            return Inner.Payload;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int[] ReadArrayInner()
        {
            return Inner.Numbers;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string[] ReadLabelsInner()
        {
            return Inner.Labels;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public object[] ReadObjectsInner()
        {
            return Inner.Objects;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ReferenceBox ReadBoxSelf()
        {
            return Inner;
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
        public static object ReadObject(ReferenceBox box)
        {
            return box.Payload;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int[] ReadArray(ReferenceBox box)
        {
            return box.Numbers;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string[] ReadLabels(ReferenceBox box)
        {
            return box.Labels;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object[] ReadObjects(ReferenceBox box)
        {
            return box.Objects;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ReferenceBox ReadBox(ReferenceOuter outer)
        {
            return outer.Inner;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string ReadShared(SharedBox box)
        {
            return box.Text;
        }
    }
}
