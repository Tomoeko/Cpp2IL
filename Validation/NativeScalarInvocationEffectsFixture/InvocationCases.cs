using System.Runtime.CompilerServices;

namespace NativeScalarInvocationEffectsFixture
{
    public class InvocationNode
    {
        public int Calls;
        public int Flushes;
        public bool Value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Set(bool value) { Calls = unchecked(Calls + 1); Value = value; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Flush() { Flushes = unchecked(Flushes + 1); }
    }

    public class InvocationHolder
    {
        public InvocationNode Target;
        public InvocationNode Other;
        public float Marker;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetThenWrite(bool value)
        {
            Target.Set(value);
            Marker = 0.5f;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetWriteThenFlush(bool value)
        {
            Target.Set(value);
            Marker = -0.0f;
            Other.Flush();
        }
    }
}
