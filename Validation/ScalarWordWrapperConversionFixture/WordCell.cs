using System.Runtime.CompilerServices;

namespace ScalarWordWrapperConversionFixture
{
    public struct WordCell
    {
        public short Value;
        public static readonly WordCell Seed = new WordCell { Value = -17 };

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static explicit operator WordCell(short value)
        {
            return new WordCell { Value = value };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static explicit operator short(WordCell value) { return value.Value; }
    }
}
