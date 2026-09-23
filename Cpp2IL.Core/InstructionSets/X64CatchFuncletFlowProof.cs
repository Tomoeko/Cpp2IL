using System;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves the two normal-control-flow exits of one bounded typed catch funclet:
/// the matched class reaches its continuation, and the unmatched class reaches
/// the native noncontinuable C++ rethrow helper. This is not a managed EH emitter.
/// </summary>
internal static class X64CatchFuncletFlowProof
{
    internal static bool Check(MethodAnalysisContext method, X64CatchFuncletClassProof.Evidence evidence)
    {
        var app = method.AppContext;
        if (app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } index ||
            index.GetHandler(method.UnderlyingPointer) is not { } region ||
            X64Eh4MapProof.Parse(pe.GetRawBinaryContent(), index, region) is not
                { TryBlocks: [{ Handlers: [{ } handler] }] } ||
            handler.NativeTypeDescriptorRva is not uint nativeType ||
            !index.IsReadableFileBackedRva(nativeType) ||
            evidence.Funclet != index.ImageBase + handler.FuncletRva)
            return false;

        var span = index.ClassifySpan(evidence.Funclet, evidence.Funclet + 1);
        if (span.Kind != X64UnwindProof.SpanKind.HandlerFree || span.Start != evidence.Funclet ||
            span.End <= span.Start || span.End - span.Start is < 96 or > 256 ||
            index.ClassifySpan(span.Start, span.End) != span)
            return false;
        var start = pe.MapVirtualAddressToRaw(span.Start, false);
        var end = pe.MapVirtualAddressToRaw(span.End - 1, false);
        var raw = pe.GetRawBinaryContent();
        if (start < 0 || end < start || (ulong)(end - start) != span.End - span.Start - 1 ||
            end >= raw.Length)
            return false;
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(
            raw.Slice((int)start, (int)(span.End - span.Start)).ToArray()), span.Start);
        var code = new Instruction[29];
        for (var n = 0; n < code.Length; n++)
        {
            code[n] = decoder.Decode();
            if (code[n].IsInvalid || code[n].NextIP > span.End)
                return false;
        }
        var metadata = app.GetOrCreateKeyFunctionAddresses().il2cpp_codegen_initialize_runtime_metadata;
        var assignable = pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_class_is_assignable_from");
        if (!X64CatchFuncletClassProof.TryMatchClassCheck(code, metadata, assignable, out var slot) ||
            slot != evidence.MetadataSlot ||
            code[15].NearBranchTarget != code[18].IP ||
            !Immediate(code[16], Register.RAX, 0) ||
            !Branch(code[17], Mnemonic.Jmp, code[24].IP) ||
            !Load(code[18], Register.RAX, Register.RDI, 0) ||
            !Store(code[19], Register.RBP, 0x20, Register.RAX) ||
            !RipAddress(code[20], Register.RDX) ||
            index.MapReadOnlyData(code[20].IPRelativeMemoryAddress, 8) < 0 ||
            !Address(code[21], Register.RCX, Register.RBP, 0x20) ||
            !Call(code[22], out var throwHelper) ||
            code[23].Mnemonic != Mnemonic.Nop ||
            !Stack(code[24], 0x20) ||
            !Pop(code[25], Register.RDI) || !Pop(code[26], Register.RBP) ||
            !Pop(code[27], Register.RBX) || code[28].Code != Code.Retnq ||
            !X64NativeCxxThrowProof.Check(pe, index, throwHelper))
            return false;
        return true;
    }

    private static bool Immediate(Instruction i, Register target, ulong value) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Register && i.Op0Register == target &&
        ((i.Op1Kind == OpKind.Immediate64 && i.Immediate64 == value) ||
         (i.Op1Kind == OpKind.Immediate32to64 && i.Immediate32 == value));

    private static bool Branch(Instruction i, Mnemonic mnemonic, ulong target) =>
        i.Mnemonic == mnemonic && i.Op0Kind == OpKind.NearBranch64 && i.NearBranchTarget == target;

    private static bool Load(Instruction i, Register target, Register basis, ulong offset) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Register && i.Op0Register == target &&
        i.Op1Kind == OpKind.Memory && i.MemoryBase == basis && i.MemoryIndex == Register.None &&
        i.MemoryDisplacement64 == offset;

    private static bool Store(Instruction i, Register basis, ulong offset, Register source) =>
        i.Mnemonic == Mnemonic.Mov && i.Op0Kind == OpKind.Memory && i.MemoryBase == basis &&
        i.MemoryIndex == Register.None && i.MemoryDisplacement64 == offset &&
        i.Op1Kind == OpKind.Register && i.Op1Register == source;

    private static bool RipAddress(Instruction i, Register target) =>
        i.Mnemonic == Mnemonic.Lea && i.Op0Kind == OpKind.Register && i.Op0Register == target &&
        i.Op1Kind == OpKind.Memory && i.MemoryBase == Register.RIP &&
        i.MemoryIndex == Register.None;

    private static bool Address(Instruction i, Register target, Register basis, ulong offset) =>
        i.Mnemonic == Mnemonic.Lea && i.Op0Kind == OpKind.Register && i.Op0Register == target &&
        i.Op1Kind == OpKind.Memory && i.MemoryBase == basis && i.MemoryIndex == Register.None &&
        i.MemoryDisplacement64 == offset;

    private static bool Call(Instruction i, out ulong target)
    {
        target = i.Code == Code.Call_rel32_64 && i.Op0Kind == OpKind.NearBranch64
            ? i.NearBranchTarget : 0;
        return target != 0;
    }

    private static bool Stack(Instruction i, ulong value) => i.Mnemonic == Mnemonic.Add &&
        i.Op0Kind == OpKind.Register && i.Op0Register == Register.RSP &&
        i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 && i.GetImmediate(1) == value;

    private static bool Pop(Instruction i, Register register) => i.Mnemonic == Mnemonic.Pop &&
        i.Op0Kind == OpKind.Register && i.Op0Register == register;
}
