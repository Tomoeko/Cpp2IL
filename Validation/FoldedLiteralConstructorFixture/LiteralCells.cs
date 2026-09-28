namespace FoldedLiteralConstructorFixture
{
    public class FirstCell
    {
        public object Neighbor;
        public int State = 37;
        public int Guard;

        public FirstCell()
        {
        }
    }

    public sealed class SecondCell
    {
        public object Neighbor;
        public int State = 37;
        public int Guard;

        public SecondCell()
        {
        }
    }

    public sealed class ThirdCell
    {
        public object Neighbor;
        public int State = -19;
        public int Guard;

        public ThirdCell()
        {
        }
    }
}
