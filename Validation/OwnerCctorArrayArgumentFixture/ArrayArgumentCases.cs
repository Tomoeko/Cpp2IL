using System.Runtime.CompilerServices;

namespace OwnerCctorArrayArgumentFixture
{
    public static class Probe
    {
        public static int Events;
    }

    public class Payload
    {
    }

    public class Cell : UnityEngine.Object
    {
        public Payload[] Items;
        public int Calls;
        public Payload Value;

        static Cell()
        {
            Probe.Events = unchecked(Probe.Events + 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Capture(Payload incoming)
        {
            Calls = unchecked(Calls + 1);
            Value = incoming;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Forward()
        {
            Capture(Items[0]);
        }
    }
}
