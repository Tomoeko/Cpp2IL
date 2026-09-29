using System.Runtime.CompilerServices;

namespace InstanceParameterReferenceReadFixture
{
    public sealed class ValueBox
    {
        public int Neighbor;
        public string Text;
        public object Payload;
        public int[] Numbers;
    }

    public sealed class Reader
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public string ReadText(ValueBox box)
        {
            return box.Text;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public object ReadPayload(ValueBox box)
        {
            return box.Payload;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int[] ReadNumbers(ValueBox box)
        {
            return box.Numbers;
        }
    }
}
