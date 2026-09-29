using System.Runtime.CompilerServices;
using System.Text;

namespace SealedParameterClassTestFixture
{
    public static class ClassTests
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsBuilder(object value)
        {
            return value is StringBuilder;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static StringBuilder AsBuilder(object value)
        {
            return value as StringBuilder;
        }
    }
}
