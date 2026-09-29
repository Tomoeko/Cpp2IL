using System;
using System.Runtime.CompilerServices;

namespace ExplicitClassCastFixture
{
    public static class CastMethods
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Exception Cast(object value)
        {
            return (Exception)value;
        }
    }
}
