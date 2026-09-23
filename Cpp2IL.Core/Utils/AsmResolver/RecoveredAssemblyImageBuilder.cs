using AsmResolver;
using AsmResolver.DotNet.Builder;

namespace Cpp2IL.Core.Utils.AsmResolver;

internal static class RecoveredAssemblyImageBuilder
{
    public static ManagedPEImageBuilder Create(IErrorListener? errors = null)
    {
        // The default writer only emits referenced dependencies. Keep declared but unused
        // AssemblyRef rows too; they are observable managed metadata, not dead code.
        const MetadataBuilderFlags flags = MetadataBuilderFlags.PreserveAssemblyReferenceIndices;
        return errors == null
            ? new ManagedPEImageBuilder(flags)
            : new ManagedPEImageBuilder(new DotNetDirectoryFactory(flags), errors);
    }
}
