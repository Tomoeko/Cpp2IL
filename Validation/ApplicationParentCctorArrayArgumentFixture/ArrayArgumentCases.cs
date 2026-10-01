using System.Runtime.CompilerServices;

namespace ApplicationParentCctorArrayArgumentFixture
{
    public static class Probe
    {
        public static int Events;
    }

    public class Parent
    {
        static Parent()
        {
            Probe.Events = unchecked(Probe.Events + 1);
        }
    }

    public class Payload
    {
    }

    public class Cell : Parent
    {
        public Payload[] Items;
        public int Calls;
        public Payload Value;

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
