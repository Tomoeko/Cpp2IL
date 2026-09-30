using System;
using System.Runtime.CompilerServices;

namespace StaticFieldGetterFixture
{
    public sealed class ReferenceHolder
    {
    }

    public static class StaticState
    {
        public const int Marker = 41;
        public const string Caption = "static getter fixture";
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

    public static class SingleFlagState
    {
        public const int Marker = 43;
        public const string Caption = "single flag fixture";
        public static bool Flag;
        public static bool NeighborFlag;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool ReadFlag()
        {
            return Flag;
        }
    }
}
