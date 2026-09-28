namespace SharedInertConstructorFixture
{
    public sealed class IntegerCell
    {
        public int First = -13;
        public int Second = 257;
        public int Third = 8191;

        public IntegerCell() { }
    }

    public sealed class MixedCell
    {
        public byte Marker = 7;
        public int Counter = -21;
        public int Offset = 41;

        public MixedCell() { }
    }
}
