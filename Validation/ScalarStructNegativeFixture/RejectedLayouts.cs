using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ScalarStructNegativeFixture
{
    [StructLayout(LayoutKind.Sequential, Size = 8)]
    public struct PaddedWord { public uint Value; }
    public struct Pair { public uint First; public uint Second; }
    public struct ReferenceWord { public object Value; }

    public static class RejectedLayouts
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Padded(PaddedWord left, PaddedWord right) { return left.Value == right.Value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Multiple(Pair left, Pair right) { return left.First == right.First; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Reference(ReferenceWord left, ReferenceWord right) { return object.ReferenceEquals(left.Value, right.Value); }
    }
}
