using System;
using System.Runtime.CompilerServices;

namespace StaticFieldGetterFixture
{
    public sealed class ReferenceHolder
    {
    }

    public static class StaticState
    {
        public static IntPtr Pointer;
        public static ReferenceHolder Reference;
        public static bool Flag;
        public static bool NeighborFlag;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static IntPtr ReadPointer()
        {
            return Pointer;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ReferenceHolder ReadReference()
        {
            return Reference;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool ReadFlag()
        {
            return Flag;
        }
    }
}
