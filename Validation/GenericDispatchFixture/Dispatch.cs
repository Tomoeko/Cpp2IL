using Neutral.GenericDispatch;

namespace GenericDispatchFixture
{
    public interface IRead<T> where T : class
    {
        T Read();
    }

    public class Forwarder : GenericDispatchBase, IRead<Payload>, IRead<string>
    {
        Payload IRead<Payload>.Read()
        {
            return Choose<Payload>(-7);
        }

        string IRead<string>.Read()
        {
            return Choose<string>(17);
        }
    }
}
