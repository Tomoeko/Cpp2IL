using System.Runtime.CompilerServices;

namespace NestedArrayCallFixture
{
    public sealed class Leaf
    {
        public bool Touched;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Touch()
        {
            Touched = true;
        }
    }

    public sealed class Node
    {
        public Leaf Link;
        public bool Touched;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Touch()
        {
            Touched = true;
        }
    }

    public sealed class NestedA
    {
        public long Before;
        public Node[] Nodes;
        public long After;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void TouchAt(int index)
        {
            Nodes[index].Link.Touch();
        }
    }

    public sealed class NestedB
    {
        public long Before;
        public Node[] Nodes;
        public long After;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void TouchAt(int index)
        {
            Nodes[index].Link.Touch();
        }
    }

    public sealed class NestedC
    {
        public long Before;
        public long Spacer;
        public Node[] Nodes;
        public long After;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void TouchAt(int index)
        {
            Nodes[index].Link.Touch();
        }
    }

    public sealed class DirectA
    {
        public long Before;
        public Node[] Nodes;
        public long After;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void TouchAt(int index)
        {
            Nodes[index].Touch();
        }
    }

    public sealed class DirectB
    {
        public long Before;
        public Node[] Nodes;
        public long After;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void TouchAt(int index)
        {
            Nodes[index].Touch();
        }
    }
}
