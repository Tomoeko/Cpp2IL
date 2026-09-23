using System.Runtime.CompilerServices;

namespace MetadataLiteralFixture
{
    public static class MetadataLiteral
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string Read() { return "neutral metadata literal"; }
    }
}
