using System.Runtime.CompilerServices;

namespace NativeScalarFieldInvocationFixture
{
    public class InvocationNode
    {
        public int Calls;
        public int Value;
        public bool Flag;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyPair(int value, bool flag) { Calls = unchecked(Calls + 1); Value = value; Flag = flag; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ApplyReverse(bool flag, int value) { Calls = unchecked(Calls + 1); Flag = flag; Value = value; }
    }

    public class InvocationHolder
    {
        public InvocationNode Target;
        public InvocationNode Replacement;
        public int Value;
        public bool Flag;
        public int Neighbor;
        public int BeforeCount;
        public int AfterCount;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetIntegerField() { Target.ApplyPair(Value, false); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetBooleanField(int value) { Target.ApplyPair(value, Flag); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetFieldPair() { Target.ApplyPair(Value, Flag); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetFieldReverse() { Target.ApplyReverse(Flag, Value); }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetFieldPairReturning()
        {
            Target.ApplyPair(Value, Flag);
            AfterCount = unchecked(AfterCount + 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void ReplaceFields()
        {
            Target = Replacement;
            Value = Neighbor;
            Flag = !Flag;
            BeforeCount = unchecked(BeforeCount + 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetCapturedBeforeEffect()
        {
            var target = Target;
            var value = Value;
            var flag = Flag;
            ReplaceFields();
            target.ApplyPair(value, flag);
            AfterCount = unchecked(AfterCount + 1);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetCapturedAfterEffect()
        {
            var target = Target;
            ReplaceFields();
            target.ApplyPair(Value, Flag);
            AfterCount = unchecked(AfterCount + 1);
        }
    }
}
