using System.Runtime.CompilerServices;

namespace NativeNullCheckedInvocationFixture
{
    public class InvocationNode
    {
        public int Calls;
        public int Value;
        public bool Flag;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Read() { Calls = unchecked(Calls + 1); return Value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadFlag() { Calls = unchecked(Calls + 1); return Flag; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyInt(int value) { Calls = unchecked(Calls + 1); Value = value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyFlag(bool value) { Calls = unchecked(Calls + 1); Flag = value; }
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
        public int ReadProduced() { return Produce().Read(); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetProducedInt(int value) { Produce().ApplyInt(value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetProducedFlag(bool value) { Produce().ApplyFlag(value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetProducedTrue() { Produce().ApplyFlag(true); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetProducedLiteral() { Produce().ApplyInt(-7); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadSnapshot() { var saved = Target; BeforeCount = unchecked(BeforeCount + 1); return saved.Read(); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetSnapshotInt(int value)
        {
            var saved = Target;
            BeforeCount = unchecked(BeforeCount + 1);
            saved.ApplyInt(value);
            AfterCount = unchecked(AfterCount + 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetSnapshotFlag(bool value)
        {
            var saved = Target;
            BeforeCount = unchecked(BeforeCount + 1);
            saved.ApplyFlag(value);
            AfterCount = unchecked(AfterCount + 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetSnapshotFalse() { var saved = Target; BeforeCount = unchecked(BeforeCount + 1); saved.ApplyFlag(false); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadReplacedSnapshot()
        {
            var saved = Target;
            Target = Replacement;
            BeforeCount = unchecked(BeforeCount + 1);
            return saved.Read();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ReadProducedFlag() { return Produce().ReadFlag(); }
    }
}
