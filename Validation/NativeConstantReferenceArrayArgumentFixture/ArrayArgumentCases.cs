using System.Runtime.CompilerServices;

namespace NativeConstantReferenceArrayArgumentFixture
{
    public class Payload
    {
    }

    public class Cell
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
