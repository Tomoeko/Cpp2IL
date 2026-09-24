using System;
using System.Collections;
using System.Runtime.CompilerServices;

namespace IteratorFactoryManualFixture
{
    public sealed class ManualOwner
    {
        public int Marker;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public IEnumerator Create()
        {
            return new ManualEnumerator(0) { Owner = this };
        }
    }

    public sealed class ManualEnumerator : IEnumerator, IDisposable
    {
        public int State;
        public ManualOwner Owner;

        public ManualEnumerator(int state)
        {
            State = state;
        }

        public object Current
        {
            get { return Owner; }
        }

        public bool MoveNext()
        {
            return false;
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }
}
