using System;
using System.Runtime.CompilerServices;

namespace ParameterClassTestFixture
{
    public static class ClassTests
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static MarshalByRefObject AsRemote(object value)
        {
            return value as MarshalByRefObject;
        }
    }
}
