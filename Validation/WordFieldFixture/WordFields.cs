using System.Runtime.CompilerServices;

namespace WordFieldFixture
{
    public sealed class WordState
    {
        public short SignedValue;
        public ushort UnsignedValue;
        public char CharacterValue;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsShortZero() { return SignedValue == 0; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsUShortZero() { return UnsignedValue == 0; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsCharZero() { return CharacterValue == '\0'; }
    }
}
