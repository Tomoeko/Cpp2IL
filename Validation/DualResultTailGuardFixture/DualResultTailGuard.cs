using System.Runtime.CompilerServices;

namespace DualResultTailGuardFixture
{
    public class FirstNode
    {
        public bool LastValue;
        public int ApplyCount;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Apply(bool value)
        {
            LastValue = value;
            ApplyCount = unchecked(ApplyCount + 1);
        }
    }

    public class SecondNode
    {
        public int FinishCount;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Finish()
        {
            FinishCount = unchecked(FinishCount + 1);
        }
    }

    public class GuardOwner
    {
        public FirstNode First;
        public SecondNode Second;
        public int FirstGetterCount;
        public int SecondGetterCount;
        public int Stage;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public FirstNode GetFirst()
        {
            FirstGetterCount = unchecked(FirstGetterCount + 1);
            Stage = 1;
            return First;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public SecondNode GetSecond()
        {
            SecondGetterCount = unchecked(SecondGetterCount + 1);
            Stage = 3;
            return Second;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual void Forward()
        {
            GetFirst().Apply(false);
            GetSecond().Finish();
        }
    }

    public sealed class FirstAliasOwner : GuardOwner
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void Forward()
        {
            GetFirst().Apply(false);
            GetSecond().Finish();
        }
    }

    public sealed class SecondAliasOwner : GuardOwner
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public override void Forward()
        {
            GetFirst().Apply(false);
            GetSecond().Finish();
        }
    }
}
