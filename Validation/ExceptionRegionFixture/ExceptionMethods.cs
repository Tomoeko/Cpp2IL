using System;
using System.Runtime.CompilerServices;

namespace ExceptionRegionFixture
{
    public static class ExceptionMethods
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int CatchZero(int dividend, int divisor)
        {
            try
            {
                if (divisor == 0)
                    throw new DivideByZeroException();
                return dividend / divisor;
            }
            catch (DivideByZeroException)
            {
                return -17;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int FinallyCount(int divisor, ref int count)
        {
            try
            {
                if (divisor == 0)
                    throw new DivideByZeroException();
                return 100 / divisor;
            }
            finally
            {
                count++;
            }
        }
    }
}
