using System.Runtime.CompilerServices;

namespace FieldGuardFixture
{
    public sealed class FieldBox
    {
        public int Value;

        public FieldBox(int value)
        {
            Value = value;
        }
    }

    public static class FieldReads
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Read(FieldBox box)
        {
            return box.Value;
        }
    }

    public static class FieldWrites
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Write(FieldBox box, int value)
        {
            box.Value = value;
        }
    }

    public sealed class LongBox
    {
        public long Value;

        public LongBox(long value)
        {
            Value = value;
        }
    }

    public static class LongFieldReads
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static long Read(LongBox box)
        {
            return box.Value;
        }
    }

    public static class LongFieldWrites
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Write(LongBox box, long value)
        {
            box.Value = value;
        }
    }

    public sealed class BooleanBox
    {
        public bool Value;
    }

    public static class BooleanFieldClears
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Clear(BooleanBox box)
        {
            box.Value = false;
        }
    }

    public sealed class NestedFieldBox
    {
        public FieldBox Inner;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ClearInner()
        {
            Inner.Value = 0;
        }
    }
}
