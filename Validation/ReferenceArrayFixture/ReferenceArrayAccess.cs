using System;
using System.Runtime.CompilerServices;

namespace ReferenceArrayFixture
{
    public static class ReferenceArrayAccess
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object ReadObject(object[] values, int index)
        {
            return values[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string ReadString(string[] values, int index)
        {
            return values[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Exception ReadClass(Exception[] values, int index)
        {
            return values[index];
        }
    }
}
