using System.Runtime.CompilerServices;

namespace ArrayElementArgumentTailFixture
{
    public sealed class ValueBox
    {
        public int Id;
    }

    public sealed class Item
    {
        public ValueBox Value;
        public int Sentinel;
    }

    public sealed class Host
    {
        public Item[] Items;
        public int CallCount;
        public ValueBox LastValue;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Consume(ValueBox value)
        {
            unchecked { CallCount++; }
            LastValue = value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Forward(int index)
        {
            Consume(Items[index].Value);
        }
    }
}
