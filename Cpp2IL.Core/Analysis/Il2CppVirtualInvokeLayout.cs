namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Layout of Il2CppClass::vtable and its two-pointer VirtualInvokeData entries.
/// The x64 offset is corroborated by the Unity 2021.3 runtime definition and
/// by the generated Windows player dispatch; the x86 offset retains the
/// resolver's existing behavior.
/// </summary>
internal static class Il2CppVirtualInvokeLayout
{
    internal const long X64VTableOffset = 0x138;
    internal const long X86VTableOffset = 0xC0;

    internal static long VTableOffset(int pointerSize) =>
        pointerSize == 8 ? X64VTableOffset : X86VTableOffset;

    internal static long EntrySize(int pointerSize) => 2L * pointerSize;
}
