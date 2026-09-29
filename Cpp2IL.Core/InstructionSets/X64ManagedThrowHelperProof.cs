using System;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Checks the small native wrappers reached by a bounded explicit managed throw.
/// This does not establish a caller's exception-region or continuation behavior.
/// </summary>
internal static class X64ManagedThrowHelperProof
{
    internal static bool Check(MethodAnalysisContext method, X64CatchDivideBodyProof.Evidence body)
        => Check(method, body.Allocator, body.NullGuard, body.Raiser);

    internal static bool Check(MethodAnalysisContext method, ulong allocator, ulong nullGuard, ulong raiser)
    {
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } index)
            return false;
        var helpers = app.GetOrCreateKeyFunctionAddresses();
        if (helpers.il2cpp_vm_object_new == 0 ||
            X64NativeInstructionReader.Read(pe, index, allocator, 1, 16) is not [var allocate] ||
            allocate.Mnemonic != Mnemonic.Jmp || allocate.Op0Kind != OpKind.NearBranch64 ||
            allocate.NearBranchTarget != helpers.il2cpp_vm_object_new ||
            X64NativeInstructionReader.Read(pe, index, nullGuard, 6, 64) == null ||
            !X64TerminalManagedThrowProof.ProveNullCheck(app, pe, index, nullGuard) ||
            !X64CodegenRaiseExceptionProof.TryIdentify(app, raiser))
            return false;
        return true;
    }
}
