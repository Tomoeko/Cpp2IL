using System.Runtime.InteropServices;

namespace ClassLayoutCollisionFixture
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct ImplicitSize
    {
        public byte First;
        public short Middle;
        public byte Last;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2, Size = 6)]
    public struct ExplicitNaturalSize
    {
        public byte First;
        public short Middle;
        public byte Last;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2, Size = 8)]
    public struct ExplicitLargerSize
    {
        public byte First;
        public short Middle;
        public byte Last;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public class ImplicitClassSize
    {
        public byte First;
        public short Middle;
        public byte Last;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2, Size = 6)]
    public class ExplicitNaturalClassSize
    {
        public byte First;
        public short Middle;
        public byte Last;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2, Size = 32)]
    public class ExplicitLargerClassSize
    {
        public byte First;
        public short Middle;
        public byte Last;
    }
}
