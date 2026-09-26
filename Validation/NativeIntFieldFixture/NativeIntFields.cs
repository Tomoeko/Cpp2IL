using System;
using System.Runtime.CompilerServices;

namespace NativeIntFieldFixture
{
    public sealed class PointerBox
    {
        public long Before;
        public IntPtr Signed;
        public UIntPtr Unsigned;
        public long After;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static IntPtr ReadSigned(PointerBox box)
        {
            return box.Signed;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static UIntPtr ReadUnsigned(PointerBox box)
        {
            return box.Unsigned;
        }
    }
}
