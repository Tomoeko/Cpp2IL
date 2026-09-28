using System.Runtime.CompilerServices;

namespace ArrayElementScalarFieldFixture
{
    public enum ToneId
    {
        First = 0,
        Second = 1,
        Third = 2
    }

    public sealed class Node
    {
        public int Level;
        public int Gap0;
        public long Gap1;
        public long Gap2;
        public float Fraction;
        public int Amount;
    }

    public sealed class Reader
    {
        public long Before;
        public long Spacer;
        public Node[] Nodes;
        public long After;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadLevel(int index)
        {
            return Nodes[index].Level;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadAmount(int index)
        {
            return Nodes[index].Amount;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public float ReadFraction(ToneId index)
        {
            return Nodes[(int)index].Fraction;
        }
    }
}
