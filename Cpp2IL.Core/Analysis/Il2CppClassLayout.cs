namespace Cpp2IL.Core.Analysis;

/// <summary>Native Il2CppClass offsets used by the existing field-storage lifter and exact x64 proofs.</summary>
internal static class Il2CppClassLayout
{
    internal const long StaticFieldsOffset64 = 0xB8;
    internal const long StaticFieldsOffset32 = 0x5C;
    // Unity 2021.3.35f1 Windows x64 Il2CppClass, corroborated by the
    // supplied runtime header and exact native hierarchy/class-init checks.
    internal const ulong TypeHierarchyOffset64 = 0xC8;
    internal const ulong TypeHierarchyDepthOffset64 = 0x12C;
    internal const ulong CctorFinishedOrNoCctorOffset64 = 0xE0;
}
