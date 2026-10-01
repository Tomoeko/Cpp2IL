using System.Runtime.CompilerServices;

namespace ClassReferenceSetterFixture
{
    public class Payload
    {
        public int Marker;
    }

    public class Cell
    {
        private Payload _current;

        public Payload Current
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return _current; }

            [MethodImpl(MethodImplOptions.NoInlining)]
            set { _current = value; }
        }
    }
}
