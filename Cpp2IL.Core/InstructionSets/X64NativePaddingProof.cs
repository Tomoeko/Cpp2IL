using System;
using System.Linq;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

internal static class X64NativePaddingProof
{
    internal static bool HasInt3Padding(PE pe, ulong start, ulong end)
    {
        if (end < start || end - start > 16)
            return false;
        if (end == start)
            return true;
        var first = pe.MapVirtualAddressToRaw(start, false);
        var last = pe.MapVirtualAddressToRaw(end - 1, false);
        var bytes = pe.GetRawBinaryContent();
        return first >= 0 && last >= first && (ulong)(last - first) == end - start - 1 &&
               last < bytes.Length && bytes.Slice((int)first, (int)(end - start)).ToArray()
                   .All(value => value == 0xCC);
    }
}
