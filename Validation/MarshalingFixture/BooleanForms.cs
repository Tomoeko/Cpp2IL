using System.Runtime.InteropServices;

namespace MarshalingFixture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct DefaultBoolean
    {
        public bool Value;
        public int After;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SignedByteBoolean
    {
        [MarshalAs(UnmanagedType.I1)] public bool Value;
        public int After;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UnsignedByteBoolean
    {
        [MarshalAs(UnmanagedType.U1)] public bool Value;
        public int After;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeBoolean
    {
        [MarshalAs(UnmanagedType.Bool)] public bool Value;
        public int After;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ScalarControls
    {
        public sbyte SignedByte;
        public byte UnsignedByte;
        public int Integer;
        public bool Boolean;
    }
}
