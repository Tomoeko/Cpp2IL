using System;
using System.Runtime.CompilerServices;

namespace NullableDelegateFieldTailFixture
{
    public sealed class DirectSignalNode
    {
        public Action Callback;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void FireIfPresent()
        {
            if (Callback != null)
                Callback();
        }
    }

    public sealed class PaddedSignalNode
    {
        public long Padding00;
        public long Padding01;
        public long Padding02;
        public long Padding03;
        public long Padding04;
        public long Padding05;
        public long Padding06;
        public long Padding07;
        public long Padding08;
        public long Padding09;
        public long Padding10;
        public long Padding11;
        public long Padding12;
        public long Padding13;
        public long Padding14;
        public long Padding15;
        public Action Callback;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void FireIfPresent()
        {
            if (Callback != null)
                Callback();
        }
    }
}
