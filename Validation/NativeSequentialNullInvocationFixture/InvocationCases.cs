using System.Runtime.CompilerServices;

namespace NativeSequentialNullInvocationFixture
{
    public class InvocationNode
    {
        public int Calls;
        public int Value;
        public bool Flag;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Ping() { Calls = unchecked(Calls + 1); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool Query() { Calls = unchecked(Calls + 1); return Flag; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyInt(int value) { Calls = unchecked(Calls + 1); Value = value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyFlag(bool value) { Calls = unchecked(Calls + 1); Flag = value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyPair(int value, bool flag) { Calls = unchecked(Calls + 1); Value = value; Flag = flag; }
    }

    public class InvocationHolder
    {
        public InvocationNode Target;
        public InvocationNode Replacement;
        public int BeforeCount;
        public int AfterCount;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SequentialInt(int value) { Target.Ping(); Target.ApplyInt(value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SequentialFlag(bool value) { Target.Ping(); Target.ApplyFlag(value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SequentialPair(int value, bool flag) { Target.Ping(); Target.ApplyPair(value, flag); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ConditionalInt(int value) { if (Target.Query()) Target.ApplyInt(value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ReplaceThenApply(int value)
        {
            Target.Ping();
            Target = Replacement;
            BeforeCount = unchecked(BeforeCount + 1);
            Target.ApplyInt(value);
            AfterCount = unchecked(AfterCount + 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void CapturedThenApply(int value)
        {
            var saved = Target;
            saved.Ping();
            Target = Replacement;
            BeforeCount = unchecked(BeforeCount + 1);
            saved.ApplyInt(value);
            AfterCount = unchecked(AfterCount + 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void DistinctReceivers(int value) { Target.Ping(); Replacement.ApplyInt(value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void RepeatedPing(int value) { Target.Ping(); Target.Ping(); Target.ApplyInt(value); }
    }
}
