using System.Runtime.CompilerServices;

namespace ArrayCallOriginsFixture
{
    public sealed class ArrayCallOperations
    {
        public int[] Values;
        public int[] Other;
        public int ProducerCalls;
        public int EffectCalls;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ArrayCallOperations() { }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int[] GetValues()
        {
            ProducerCalls++;
            return Values;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int[] GetOther()
        {
            ProducerCalls++;
            return Other;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ChangeValue(int index, int value)
        {
            Values[index] = value;
            EffectCalls++;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReadProduced(ArrayCallOperations owner, int index)
        {
            return owner.GetValues()[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int CaptureBeforeEffect(ArrayCallOperations owner, int index, int value)
        {
            var captured = owner.GetValues();
            owner.ChangeValue(index, value);
            return captured[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReadAfterEffect(ArrayCallOperations owner, int index, int value)
        {
            owner.ChangeValue(index, value);
            return owner.GetValues()[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReadAroundEffect(ArrayCallOperations owner, int index, int value)
        {
            var first = owner.GetValues()[index];
            owner.ChangeValue(index, value);
            return first + owner.GetValues()[index];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReadPairCaptured(ArrayCallOperations owner, int index)
        {
            var left = owner.GetValues();
            var right = owner.GetOther();
            return left[index] + right[index];
        }
    }
}
