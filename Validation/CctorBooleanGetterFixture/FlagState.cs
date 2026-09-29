using System.Runtime.CompilerServices;

namespace CctorBooleanGetterFixture
{
    public sealed class FlagState
    {
        public bool Flag;
        public bool Neighbor;
        public int Counter;

        // An explicit constructor gives the owner a real class-initialization
        // boundary while leaving the instance getter's behavior independent of it.
        static FlagState()
        {
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadFlag()
        {
            return Flag;
        }
    }
}
