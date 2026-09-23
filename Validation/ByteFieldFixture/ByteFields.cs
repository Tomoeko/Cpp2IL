using System.Runtime.CompilerServices;

namespace ByteFieldFixture
{
    public sealed class ByteFields
    {
        public bool Condition;
        public bool Observed;
        public byte Value;
        public sbyte Signed;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ObserveCondition()
        {
            if (Condition)
                Observed = true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsByteZero() { return Value == 0; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsSignedZero() { return Signed == 0; }
    }
}
