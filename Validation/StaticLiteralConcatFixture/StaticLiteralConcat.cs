using System.Runtime.CompilerServices;

namespace StaticLiteralConcatFixture
{
    public static class LiteralJoiner
    {
        public static int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string AppendLiteral(string value)
        {
            return value + "|tag";
        }
    }
}
