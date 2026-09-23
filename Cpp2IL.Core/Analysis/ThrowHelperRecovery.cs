using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Compatibility entry points for retired exception-helper heuristics, and bounded C-string reads.
/// Names and transitive call reachability do not establish a helper's managed semantics.
/// </summary>
public static class ThrowHelperRecovery
{
    private const int MaxStringLength = 64;

    /// <summary>
    /// Returns no match: an exception name does not prove a constructor, its arguments, or an
    /// unconditional throw. Retained for callers of the former public heuristic API.
    /// </summary>
    public static TypeAnalysisContext? GetThrownException(ApplicationAnalysisContext appContext, ulong address) => null;

    /// <summary>
    /// Returns no match: reaching a native raiser does not prove argument identity, absence of
    /// other effects, or nonreturn on every path. Legacy cached hints are not a proof.
    /// </summary>
    public static bool IsExceptionRaiser(ApplicationAnalysisContext appContext, ulong address) => false;

    //TODO didn't we have a helper for this somewhere? Can't find it. Maybe got deleted. Maybe it's just too late
    internal static string? ReadCStringAtVirtualAddress(ApplicationAnalysisContext appContext, ulong address, int maxLength = MaxStringLength)
    {
        long offset;

        try
        {
            offset = appContext.Binary.MapVirtualAddressToRaw(address, false);
        }
        catch
        {
            return null;
        }

        if (offset <= 0)
            return null;

        var content = appContext.Binary.GetRawBinaryContent();
        var end = offset;

        while (end < content.Length && end - offset < maxLength && content[(int)end] != 0)
        {
            var c = content[(int)end];

            if (c < 32 || c >= 127)
                return null;

            end++;
        }

        if (end == offset || end >= content.Length || content[(int)end] != 0)
            return null;

        var characters = new char[end - offset];

        for (var i = 0; i < characters.Length; i++)
            characters[i] = (char)content[(int)offset + i];

        return new string(characters);
    }
}
