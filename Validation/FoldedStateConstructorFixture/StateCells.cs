namespace FoldedStateConstructorFixture
{
    public sealed class FirstCell
    {
        public int State;
        public object Neighbor;

        public FirstCell(int state)
        {
            State = state;
        }
    }

    public sealed class SecondCell
    {
        public int State;
        public object Neighbor;

        public SecondCell(int state)
        {
            State = state;
        }
    }
}
