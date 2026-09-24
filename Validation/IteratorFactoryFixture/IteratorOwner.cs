using System.Collections;
using System.Runtime.CompilerServices;

namespace IteratorFactoryFixture
{
    public sealed class IteratorOwner
    {
        public object Marker;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public IEnumerator Iterate()
        {
            yield return Marker;
        }
    }
}
