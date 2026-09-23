namespace DeclarationOnlyFixture
{
    public enum Mode : short
    {
        None = 0,
        Active = 7
    }

    public interface ISource<T> where T : class
    {
        T Read(Mode mode);
    }

    public struct Pair
    {
        public int Left;
        public int Right;
    }
}
