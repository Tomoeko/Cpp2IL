using System.Runtime.CompilerServices;
using Neutral.Package;
using Neutral.Plugin;

namespace ExternalReferenceFixture
{
    public static class ReferenceKinds
    {
        public static PackageNode Package;
        public static PluginNode Plugin;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Increment(int value)
        {
            return value + 1;
        }
    }
}
