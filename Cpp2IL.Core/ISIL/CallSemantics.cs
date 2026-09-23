namespace Cpp2IL.Core.ISIL;

/// <summary>Managed invocation behavior retained independently of the native call opcode.</summary>
public enum CallSemantics
{
    Direct,
    // A proved runtime receiver guard must remain a null check after native guard coalescing.
    NullCheckedInstance,
}
