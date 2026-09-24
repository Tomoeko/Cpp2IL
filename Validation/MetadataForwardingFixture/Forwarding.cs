using System;
using System.Runtime.CompilerServices;

namespace MetadataForwardingFixture
{
    // This type deliberately has no static constructor or field initializer.
    public static class SharedState
    {
        public static object Current;
        public static object Neighbor;
    }

    public static class Preparation
    {
        public static int Calls;
        public static int Clock;
        public static int LastOrder;
        public static bool ThrowNext;
        public static bool ReplaceNext;
        public static object Replacement;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Prepare()
        {
            Calls++;
            LastOrder = ++Clock;
            if (ThrowNext)
                throw new InvalidOperationException("preparation failed");
            if (ReplaceNext)
            {
                SharedState.Current = Replacement;
                ReplaceNext = false;
            }
        }
    }

    public static class Receiver
    {
        public static int Calls;
        public static int LastOrder;
        public static int LastPrepareCalls;
        public static int LastNumber;
        public static object LastReference;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Receive(object value)
        {
            Calls++;
            LastOrder = ++Preparation.Clock;
            LastPrepareCalls = Preparation.Calls;
            LastReference = value;
            return value != null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Receive(object value, int number)
        {
            Calls++;
            LastOrder = ++Preparation.Clock;
            LastPrepareCalls = Preparation.Calls;
            LastReference = value;
            LastNumber = number;
            return value != null && number < 0;
        }
    }

    public static class Forwarder
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Forward()
        {
            Preparation.Prepare();
            return Receiver.Receive(SharedState.Current);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Forward(int number)
        {
            Preparation.Prepare();
            return Receiver.Receive(SharedState.Current, number);
        }
    }
}
