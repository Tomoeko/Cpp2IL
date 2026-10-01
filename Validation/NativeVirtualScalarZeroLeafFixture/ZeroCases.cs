using System.Runtime.CompilerServices;

namespace NativeVirtualScalarZeroLeafFixture
{
    public interface IZeroPair
    {
        float First { get; }
        float Second { get; }
    }

    public class ZeroPair : IZeroPair
    {
        public static readonly int Marker = 17;

        public virtual float First
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return 0f; }
        }

        public virtual float Second
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return 0f; }
        }

        public interface IStringZero
        {
            float Read(string value);
        }

        public sealed class StringZero : IStringZero
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public float Read(string value) { return 0f; }
        }
    }

    public sealed class DerivedZero : ZeroPair
    {
        public override float First
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return 0f; }
        }

        public override float Second
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get { return 0f; }
        }
    }
}
