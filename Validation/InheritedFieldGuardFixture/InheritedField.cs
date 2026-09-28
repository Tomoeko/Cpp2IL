using System.Runtime.CompilerServices;

namespace InheritedFieldGuardFixture
{
    public class BaseCell
    {
        public int Value;
    }

    public sealed class DerivedCell : BaseCell
    {
        public int Neighbor;
    }

    public static class InheritedFieldReads
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Read(DerivedCell cell)
        {
            return cell.Value;
        }
    }
}
