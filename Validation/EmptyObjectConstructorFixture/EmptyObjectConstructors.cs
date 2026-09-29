using System.Runtime.CompilerServices;

namespace EmptyObjectConstructorFixture
{
    public sealed class EmptyCell
    {
        public int Number;
        public bool Flag;
        public object Payload;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public EmptyCell()
        {
        }
    }

    public sealed class EmptySibling
    {
        public long Count;
        public string Label;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public EmptySibling()
        {
        }
    }
}
