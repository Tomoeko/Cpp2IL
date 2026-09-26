using System.Runtime.CompilerServices;

namespace FloatForwardStoreFixture
{
    public sealed class FloatTarget
    {
        public long Before;
        private float _level;
        public long After;

        public void SetLevel(float value)
        {
            _level = value;
        }

        public float ReadLevel()
        {
            return _level;
        }
    }

    public sealed class FloatOwner
    {
        public long Before;
        public FloatTarget Target;
        public long After;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Forward(float value)
        {
            Target.SetLevel(value);
        }
    }
}
