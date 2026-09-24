using System;
using System.Runtime.CompilerServices;
using Neutral.GuardedSink;

namespace GuardedSinkFixture
{
    public sealed class Forwarder
    {
        public object Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SendNorth(Exception exception)
        {
            GuardedSink.Accept("north", exception);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SendSouth(Exception exception)
        {
            GuardedSink.Accept("south", exception);
        }
    }
}
