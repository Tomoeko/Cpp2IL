using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Identifies the managed class tested by one bounded x64 catch funclet. This is
/// class-check evidence, not a recovered catch clause or permission to lift the caller.
/// </summary>
internal static class X64CatchFuncletClassProof
{
    internal sealed record Evidence(TypeAnalysisContext CheckedClass, ulong MetadataSlot,
        ulong Funclet, int? ConstantContinuationReturn);

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (app.Binary is not PE { PointerSizeBytes: 8 } pe ||
            app.UnityVersion.ToString() != "2021.3.35f1" ||
            method.UnderlyingPointer == 0 ||
            X64UnwindProof.ForApplication(app) is not { } index ||
            index.GetHandler(method.UnderlyingPointer) is not { } region ||
            X64Eh4MapProof.Parse(pe.GetRawBinaryContent(), index, region) is not
            { TryBlocks: [{ Handlers: [{ } handler] }], UnwindActions: var actions } ||
            actions.Any(action => action.Kind != 0) ||
            handler.FuncletRva == 0 || handler.FuncletRva >= ulong.MaxValue - index.ImageBase)
            return null;

        var funclet = index.ImageBase + handler.FuncletRva;
        var span = index.ClassifySpan(funclet, funclet + 1);
        if (span.Kind != X64UnwindProof.SpanKind.HandlerFree || span.Start != funclet ||
            span.End <= funclet || span.End - funclet is < 32 or > 256 ||
            index.ClassifySpan(funclet, span.End) != span)
            return null;
        var rawStart = pe.MapVirtualAddressToRaw(funclet, false);
        var rawEnd = pe.MapVirtualAddressToRaw(span.End - 1, false);
        var raw = pe.GetRawBinaryContent();
        if (rawStart < 0 || rawEnd < rawStart ||
            (ulong)(rawEnd - rawStart) != span.End - funclet - 1 ||
            rawEnd >= raw.Length)
            return null;
        var length = checked((int)(span.End - funclet));
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(raw.Slice(checked((int)rawStart), length).ToArray()), funclet);
        var prefix = new Instruction[16];
        for (var i = 0; i < prefix.Length; i++)
        {
            prefix[i] = decoder.Decode();
            if (prefix[i].IsInvalid || prefix[i].NextIP > span.End)
                return null;
        }

        var helpers = app.GetOrCreateKeyFunctionAddresses();
        var metadata = helpers.il2cpp_codegen_initialize_runtime_metadata;
        var assignable = pe.GetVirtualAddressOfExportedFunctionByName("il2cpp_class_is_assignable_from");
        if (!TryMatchClassCheck(prefix, metadata, assignable, out var slot) ||
            app.LibCpp2IlContext.GetTypeGlobalByAddress(slot) is not { } rawType ||
            app.ResolveIl2CppType(rawType) is not { Definition: not null } checkedClass ||
            !ISIL.NullCheckedCall.IsReferenceClass(checkedClass))
            return null;
        var continuationReturn = ReferenceEquals(method.ReturnType, app.SystemTypes.SystemInt32Type)
            ? ReadConstantContinuationReturn(pe, index, region, handler)
            : null;
        return new Evidence(checkedClass, slot, funclet, continuationReturn);
    }

    private static int? ReadConstantContinuationReturn(PE pe, X64UnwindProof.Index index,
        X64UnwindProof.HandlerInfo region, X64Eh4MapProof.Handler handler)
    {
        if (handler.ContinuationRvas is not [var rva] ||
            rva > ulong.MaxValue - index.ImageBase)
            return null;
        var continuation = index.ImageBase + rva;
        if (continuation < region.Start || continuation >= region.End)
            return null;
        var length = (int)Math.Min(region.End - continuation, 32UL);
        if (length < 11 || index.ClassifySpan(continuation, continuation + (ulong)length).Kind !=
            X64UnwindProof.SpanKind.Unsupported)
            return null;
        var rawStart = pe.MapVirtualAddressToRaw(continuation, false);
        var rawEnd = pe.MapVirtualAddressToRaw(continuation + (ulong)length - 1, false);
        var raw = pe.GetRawBinaryContent();
        if (rawStart < 0 || rawEnd < rawStart || rawEnd - rawStart != length - 1 || rawEnd >= raw.Length)
            return null;
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(raw.Slice((int)rawStart, length).ToArray()), continuation);
        var instructions = new Instruction[4];
        for (var i = 0; i < instructions.Length; i++)
        {
            instructions[i] = decoder.Decode();
            if (instructions[i].IsInvalid || instructions[i].NextIP > continuation + (ulong)length)
                return null;
        }
        return instructions[0].Mnemonic == Mnemonic.Mov && instructions[0].Op0Kind == OpKind.Register &&
               instructions[0].Op0Register == Register.EAX && instructions[0].Op1Kind == OpKind.Immediate32 &&
               instructions[1].Mnemonic == Mnemonic.Add && instructions[1].Op0Kind == OpKind.Register &&
               instructions[1].Op0Register == Register.RSP &&
               instructions[1].Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
               instructions[1].GetImmediate(1) == 0x30 &&
               instructions[2].Mnemonic == Mnemonic.Pop && instructions[2].Op0Kind == OpKind.Register &&
               instructions[2].Op0Register == Register.RBX && instructions[3].Code == Code.Retnq
            ? unchecked((int)instructions[0].Immediate32)
            : null;
    }

    internal static bool TryMatchClassCheck(IReadOnlyList<Instruction> code, ulong metadataHelper,
        ulong assignableExport, out ulong slot)
    {
        slot = 0;
        if (code.Count < 16 || metadataHelper == 0 || assignableExport == 0 ||
            !Store(code[0], Register.RSP, 0x10, Register.RDX) ||
            !Push(code[1], Register.RBX) || !Push(code[2], Register.RBP) ||
            !Push(code[3], Register.RDI) || !Stack(code[4], 0x20) ||
            !Move(code[5], Register.RBP, Register.RDX) ||
            !Load(code[6], Register.RDI, Register.RBP, 0x28) ||
            !Load(code[7], Register.RAX, Register.RDI, 0) ||
            !Load(code[8], Register.RBX, Register.RAX, 0) ||
            code[9].Mnemonic != Mnemonic.Lea || code[9].Op0Kind != OpKind.Register ||
            code[9].Op0Register != Register.RCX || code[9].Op1Kind != OpKind.Memory ||
            code[9].MemoryBase != Register.RIP || code[9].MemoryIndex != Register.None ||
            code[9].SegmentPrefix != Register.None ||
            !Call(code[10], metadataHelper) ||
            !Move(code[11], Register.RDX, Register.RBX) ||
            !Move(code[12], Register.RCX, Register.RAX) ||
            !Call(code[13], assignableExport) ||
            !MoveTest(code[14], Register.AL) ||
            code[15].Mnemonic != Mnemonic.Je || code[15].Op0Kind != OpKind.NearBranch64 ||
            code[15].NearBranchTarget <= code[15].IP)
            return false;
        slot = code[9].IPRelativeMemoryAddress;
        return slot != 0;
    }

    private static bool Push(Instruction i, Register register) => i.Mnemonic == Mnemonic.Push &&
        i.OpCount == 1 && i.Op0Kind == OpKind.Register && i.Op0Register == register;

    private static bool Stack(Instruction i, ulong size) => i.Mnemonic == Mnemonic.Sub &&
        i.OpCount == 2 && i.Op0Kind == OpKind.Register && i.Op0Register == Register.RSP &&
        i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 && i.GetImmediate(1) == size;

    private static bool Move(Instruction i, Register destination, Register source) =>
        i.Mnemonic == Mnemonic.Mov && i.OpCount == 2 &&
        i.Op0Kind == OpKind.Register && i.Op0Register == destination &&
        i.Op1Kind == OpKind.Register && i.Op1Register == source;

    private static bool Memory(Instruction i, int operand, Register basis, ulong displacement) =>
        i.GetOpKind(operand) == OpKind.Memory && i.MemoryBase == basis &&
        i.MemoryIndex == Register.None && i.SegmentPrefix == Register.None &&
        i.MemoryDisplacement64 == displacement && i.MemorySize.GetSize() == 8;

    private static bool Store(Instruction i, Register basis, ulong displacement, Register value) =>
        i.Mnemonic == Mnemonic.Mov && i.OpCount == 2 && Memory(i, 0, basis, displacement) &&
        i.Op1Kind == OpKind.Register && i.Op1Register == value;

    private static bool Load(Instruction i, Register destination, Register basis, ulong displacement) =>
        i.Mnemonic == Mnemonic.Mov && i.OpCount == 2 && i.Op0Kind == OpKind.Register &&
        i.Op0Register == destination && Memory(i, 1, basis, displacement);

    private static bool Call(Instruction i, ulong target) => i.Code == Code.Call_rel32_64 &&
        i.Op0Kind == OpKind.NearBranch64 && i.NearBranchTarget == target;

    private static bool MoveTest(Instruction i, Register register) => i.Mnemonic == Mnemonic.Test &&
        i.OpCount == 2 && i.Op0Kind == OpKind.Register && i.Op0Register == register &&
        i.Op1Kind == OpKind.Register && i.Op1Register == register;
}
