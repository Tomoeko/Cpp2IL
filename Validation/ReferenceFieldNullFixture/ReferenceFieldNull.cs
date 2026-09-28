using System.Runtime.CompilerServices;

namespace ReferenceFieldNullFixture
{
    public sealed class OperatorTrap
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool operator ==(OperatorTrap left, OperatorTrap right)
        {
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool operator !=(OperatorTrap left, OperatorTrap right)
        {
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override bool Equals(object other)
        {
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public override int GetHashCode()
        {
            return 0;
        }
    }

    public class ReferenceOwner
    {
        public object Current;
        public string Text;
        public OperatorTrap Trap;
        public int Neighbor;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsCurrentNull()
        {
            return Current == null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool HasCurrent()
        {
            return Current != null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsTextNull()
        {
            return Text == null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsTrapReferenceNull()
        {
            return object.ReferenceEquals(Trap, null);
        }

    }

    public sealed class DerivedOwner : ReferenceOwner
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool IsInheritedCurrentNull()
        {
            return Current == null;
        }
    }
}
