using System.Runtime.CompilerServices;

namespace Neutral.GenericDispatch
{
    public sealed class Payload
    {
    }

    public class GenericDispatchBase
    {
        private int _calls;
        private int _lastKey;
        private object _neighbor;

        public int Calls => _calls;
        public int LastKey => _lastKey;

        public object Neighbor
        {
            get { return _neighbor; }
            set { _neighbor = value; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        protected T Choose<T>(int key) where T : class
        {
            _calls++;
            _lastKey = key;
            return null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        protected object Choose(int key)
        {
            _calls++;
            _lastKey = key;
            return null;
        }

        public object InvokeNongeneric(int key)
        {
            return Choose(key);
        }
    }
}
