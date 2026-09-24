using System.Runtime.InteropServices;

namespace PackOnlyClassFixture
{
    // These declarations intentionally omit Size. The controls test whether
    // field shape changes the player class-size default flag.
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public sealed class Empty
    {
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public sealed class OneByte
    {
        public byte Value;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public sealed class ThreeFields
    {
        public byte First;
        public short Middle;
        public byte Last;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public sealed class OneReference
    {
        public object Value;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 2)]
    public sealed class ExplicitOneByte
    {
        [FieldOffset(0)]
        public byte Value;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2, Size = 0)]
    public sealed class ExplicitZero
    {
        public byte Value;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2, Size = 32)]
    public sealed class LargerSize
    {
        public byte Value;
    }
}
