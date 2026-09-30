using System.Runtime.CompilerServices;

namespace NativeScalarPairInvocationFixture
{
    public class InvocationNode
    {
        public int Calls;
        public int Value;
        public bool Flag;
        public int Neighbor;
        public bool SecondFlag;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyPair(int value, bool flag) { Calls = unchecked(Calls + 1); Value = value; Flag = flag; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyReverse(bool flag, int value) { Calls = unchecked(Calls + 1); Flag = flag; Value = value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyIntegers(int value, int neighbor) { Calls = unchecked(Calls + 1); Value = value; Neighbor = neighbor; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyFlags(bool flag, bool secondFlag) { Calls = unchecked(Calls + 1); Flag = flag; SecondFlag = secondFlag; }
    }

    public class InvocationHolder
    {
        public InvocationNode Target;
        public InvocationNode Replacement;
        public int ProducerCount;
        public int BeforeCount;
        public int AfterCount;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public InvocationNode Produce() { ProducerCount = unchecked(ProducerCount + 1); return Target; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetProducedPair(int value, bool flag) { Produce().ApplyPair(value, flag); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetProducedReverse(int value, bool flag) { Produce().ApplyReverse(flag, value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetProducedIntegers(int value, bool flag) { Produce().ApplyIntegers(value, -7); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetProducedFlags(int value, bool flag) { Produce().ApplyFlags(flag, true); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetSnapshotPair(int value, bool flag)
        {
            var saved = Target;
            BeforeCount = unchecked(BeforeCount + 1);
            saved.ApplyPair(value, flag);
            AfterCount = unchecked(AfterCount + 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetSnapshotReverse(int value, bool flag)
        {
            var saved = Target;
            BeforeCount = unchecked(BeforeCount + 1);
            saved.ApplyReverse(flag, value);
            AfterCount = unchecked(AfterCount + 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetReplacedSnapshot(int value, bool flag)
        {
            var saved = Target;
            Target = Replacement;
            BeforeCount = unchecked(BeforeCount + 1);
            saved.ApplyPair(value, flag);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetSnapshotLiterals()
        {
            var saved = Target;
            BeforeCount = unchecked(BeforeCount + 1);
            saved.ApplyPair(-7, false);
        }
    }
}
