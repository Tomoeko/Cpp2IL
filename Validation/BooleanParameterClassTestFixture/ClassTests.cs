using System;
using System.Runtime.CompilerServices;

namespace BooleanParameterClassTestFixture
{
    public static class ClassTests
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsRemote(object value)
        {
            return value is MarshalByRefObject;
        }
    }
}
