using System;
using System.Runtime.CompilerServices;

namespace Neutral.GuardedSink
{
    public static class SinkWitness
    {
        public static int Initializations;
        public static int Calls;
        public static int EventCount;
        public static int InitializationOrder;
        public static int LastCallOrder;
        public static int InitializationsAtLastCall;
        public static string LastLiteral;
        public static Exception LastException;
    }

    public static class GuardedSink
    {
        static GuardedSink()
        {
            SinkWitness.Initializations++;
            SinkWitness.InitializationOrder = ++SinkWitness.EventCount;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Accept(string literal, Exception exception)
        {
            SinkWitness.Calls++;
            SinkWitness.LastCallOrder = ++SinkWitness.EventCount;
            SinkWitness.InitializationsAtLastCall = SinkWitness.Initializations;
            SinkWitness.LastLiteral = literal;
            SinkWitness.LastException = exception;
        }
    }
}
