using System.Runtime.CompilerServices;

namespace ScalarStructFixture
{
    public struct Word32 { public uint Value; }
    public struct Word64 { public ulong Value; }

    public static class ScalarStructs
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Equal32(Word32 left, Word32 right) { return left.Value == right.Value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint Add32(Word32 left, Word32 right) { return unchecked(left.Value + right.Value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Equal64(Word64 left, Word64 right) { return left.Value == right.Value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ulong Add64(Word64 left, Word64 right) { return unchecked(left.Value + right.Value); }
    }
}
