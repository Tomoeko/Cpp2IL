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
}
