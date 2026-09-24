using System.Runtime.CompilerServices;

namespace DenseSwitchFixture
{
    public static class SwitchMethods
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Dense(int selector, int value, ref int trace)
        {
            // Keep the case bodies distinct so the exact native build can reveal
            // how its optimizer lays out a dense multiway branch.
            unchecked
            {
                switch (selector)
                {
                    case 0:
                        trace = trace * 3 + 101;
                        return value + trace;
                    case 1:
                        trace = trace * 5 - 37;
                        return value - trace;
                    case 2:
                        trace ^= 0x4a391827;
                        return value ^ trace;
                    case 3:
                        trace = trace + value + 211;
                        return value | trace;
                    case 4:
                        trace = trace - value - 313;
                        return value & trace;
                    case 5:
                        trace = trace * 7 + 419;
                        return (value << 3) + trace;
                    case 6:
                        trace = trace * 11 - 521;
                        return (value >> 2) ^ trace;
                    case 7:
                        trace ^= value << 1;
                        return value * (trace | 1);
                    case 8:
                        trace += value >> 3;
                        return -value + trace;
                    case 9:
                        trace -= value << 2;
                        return (value << 5) | (trace >> 27);
                    case 10:
                        trace = trace * 13 + 631;
                        return value - (trace >> 4);
                    case 11:
                        trace = (trace >> 1) ^ value;
                        return (value & trace) + 701;
                    case 12:
                        trace += 809;
                        return (value ^ (value >> 16)) + trace;
                    case 13:
                        trace -= 907;
                        return (value * 17) ^ trace;
                    case 14:
                        trace = trace * 17 + 1009;
                        return (value | 0x55aa55aa) - trace;
                    case 15:
                        trace ^= 1103;
                        return (value & 0x7f7f7f7f) + (trace << 1);
                    default:
                        trace += 1201;
                        return value ^ trace;
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Sparse(int selector, int value, ref int trace)
        {
            unchecked
            {
                switch (selector)
                {
                    case -100000:
                        trace += 17;
                        return value + trace;
                    case -17:
                        trace ^= 0x13579bdf;
                        return value ^ trace;
                    case 0:
                        trace -= 23;
                        return value - trace;
                    case 29:
                        trace *= 7;
                        return value * trace;
                    case 100000:
                        trace += value;
                        return value | trace;
                    case int.MaxValue:
                        trace -= value;
                        return value & trace;
                    default:
                        trace ^= 41;
                        return value + trace;
                }
            }
        }
    }
}
