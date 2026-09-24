using System;
using System.Collections;
using System.Runtime.CompilerServices;

namespace IteratorFactoryDirectCtorFixture
{
    public sealed class FactoryOwner
    {
        public int Marker;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public IEnumerator Create()
        {
            var enumerator = new DirectStateEnumerator(0);
            enumerator.Owner = this;
            return enumerator;
        }
    }

    public sealed class DirectStateEnumerator : IEnumerator, IDisposable
    {
        public int State;
        public object Neighbor;
        public FactoryOwner Owner;

        public DirectStateEnumerator()
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public DirectStateEnumerator(int state)
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
