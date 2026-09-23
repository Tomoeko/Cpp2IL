namespace Cpp2IL.Core.Analysis;

/// <summary>Native Il2CppClass offsets used by the existing field-storage lifter and exact x64 proofs.</summary>
internal static class Il2CppClassLayout
{
    internal const long StaticFieldsOffset64 = 0xB8;
    internal const long StaticFieldsOffset32 = 0x5C;
}
