using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Qualifies scalar register selections whose preserved upper lanes cannot escape.</summary>
internal static class X64ScalarFloatSelectionProof
{
    internal static bool CanLift(MethodAnalysisContext? method, Instruction site)
        => IsSelection(site) && CanProject(method, site, out _);

    internal static bool CanProject(MethodAnalysisContext? method, Instruction site, out int width)
    {
        width = 0;
        if (method == null || !IsPossibleProjectionSite(site) || !HasPossibleScalarAnchor(method) ||
            X64UnwindProof.ForApplication(method.AppContext) == null ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) ||
            !ReferenceEquals(method.ReturnType, method.DefaultReturnType) || method.OverrideReturnType != null)
            return false;
        var body = ReadBody(method);
        var anchor = body?.FirstOrDefault(instruction => IsSelection(instruction) ||
            instruction.Mnemonic is Mnemonic.Comiss or Mnemonic.Ucomiss or Mnemonic.Comisd or Mnemonic.Ucomisd);
        if (anchor is not { } scalar || scalar.Length == 0)
            return false;
        var noReturn = new HashSet<ulong>(body!.Where(instruction => instruction.Code == Code.Call_rel32_64 &&
                X86RuntimeNullThrowProof.TryIdentify(method.AppContext, instruction.NearBranchTarget) != null)
            .Select(instruction => instruction.IP));
        width = scalar.Mnemonic is Mnemonic.Minss or Mnemonic.Maxss or Mnemonic.Comiss or Mnemonic.Ucomiss ? 32 : 64;
        if (ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemSingleType) &&
            (width != 32 || method.Definition!.RawReturnType!.Type != Il2CppTypeEnum.IL2CPP_TYPE_R4) ||
            ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemDoubleType) &&
            (width != 64 || method.Definition!.RawReturnType!.Type != Il2CppTypeEnum.IL2CPP_TYPE_R8) ||
            method.Parameters.Any(parameter =>
                ReferenceEquals(parameter.DefaultParameterType, method.AppContext.SystemTypes.SystemSingleType) &&
                parameter.Definition!.RawType!.Type != Il2CppTypeEnum.IL2CPP_TYPE_R4 ||
                ReferenceEquals(parameter.DefaultParameterType, method.AppContext.SystemTypes.SystemDoubleType) &&
                parameter.Definition!.RawType!.Type != Il2CppTypeEnum.IL2CPP_TYPE_R8))
            return false;
        return body != null && body.Any(instruction => instruction.IP == site.IP &&
                instruction.NextIP == site.NextIP && instruction.Equals(site)) &&
            IsScalarBody(body, scalar.IP, noReturn) &&
            X86CallerExceptionRegionProof.Check(method, body, noReturn) == null;
    }

    internal static bool IsPossibleProjectionSite(Instruction site)
    {
        if (site.IsInvalid || site.HasLockPrefix || site.HasRepPrefix || site.HasRepnePrefix ||
            site.SegmentPrefix != Register.None)
            return false;
        if (site.Mnemonic is not (Mnemonic.Movaps or Mnemonic.Movapd or Mnemonic.Movups or Mnemonic.Movupd or
            Mnemonic.Xorps or Mnemonic.Xorpd))
            return true; // Scalar field-store consumers still authenticate their own memory operands.
        return site.OpCount == 2 && site.Op0Kind == OpKind.Register && site.Op1Kind == OpKind.Register &&
               IsVolatileScalarRegister(site.Op0Register) && IsVolatileScalarRegister(site.Op1Register) &&
               (site.Mnemonic is not (Mnemonic.Xorps or Mnemonic.Xorpd) ||
                site.Op0Register == site.Op1Register);
    }

    private static bool HasPossibleScalarAnchor(MethodAnalysisContext method)
    {
        if (method.AppContext.Binary is not PE { PointerSizeBytes: 8 } pe || method.UnderlyingPointer == 0)
            return false;
        try
        {
            var offset = pe.MapVirtualAddressToRaw(method.UnderlyingPointer, false);
            var image = pe.GetRawBinaryContent();
            if (offset < 0 || offset >= image.Length)
                return false;
            // This prefix is only a rejection filter, never native evidence. It
            // covers every byte accepted by ReadBody, including a root extending
            // beyond the metadata-derived cache. A neighboring anchor can only
            // trigger the unchanged complete authentication below.
            var bytes = image.Slice(checked((int)offset), Math.Min(4096, image.Length - checked((int)offset)));
            return X86Utils.Iterate(bytes, method.UnderlyingPointer, false).Any(instruction =>
                IsSelection(instruction) || instruction.Mnemonic is
                    Mnemonic.Comiss or Mnemonic.Ucomiss or Mnemonic.Comisd or Mnemonic.Ucomisd);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static Instruction[]? ReadBody(MethodAnalysisContext method)
    {
        if (X64NativeInstructionReader.ReadRootBody(method) is { } root)
            return root;
        var prefix = X86Utils.Iterate(method).Take(64).TakeWhile(i => i.Mnemonic != Mnemonic.Int3).ToArray();
        var returnIndex = System.Array.FindIndex(prefix, i => i.Code == Code.Retnq);
        return returnIndex >= 0 && prefix[returnIndex].NextIP - method.UnderlyingPointer <= 256
            ? X64NativeInstructionReader.ReadFramelessBody(method, returnIndex + 1, 256) : null;
    }

    internal static bool IsSelection(Instruction instruction) =>
        instruction.Code is Code.Minss_xmm_xmmm32 or Code.Maxss_xmm_xmmm32 or
            Code.Minsd_xmm_xmmm64 or Code.Maxsd_xmm_xmmm64 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
        IsVolatileScalarRegister(instruction.Op0Register) && IsVolatileScalarRegister(instruction.Op1Register) &&
        !instruction.HasLockPrefix && !instruction.HasRepPrefix && !instruction.HasRepnePrefix &&
        instruction.SegmentPrefix == Register.None;

    // This deliberately excludes returning calls, packed arithmetic, lane extraction,
    // full-width memory copies and nonvolatile spills. Those require additional
    // lane/ABI proofs; typed low-lane operands alone cannot establish them.
    internal static bool IsScalarBody(IReadOnlyList<Instruction>? body, ulong selectionIp,
        ISet<ulong>? noReturnNullCalls = null)
    {
        // The root reader authenticates the complete unwind range, including
        // alignment traps after terminal control flow. Keep that full snapshot
        // for caller coverage, but exclude only its trailing INT3 instructions
        // from the scalar shape. Branch targets must remain inside this prefix.
        if (body != null)
        {
            var count = body.Count;
            while (count != 0 && body[count - 1].Code == Code.Int3)
                count--;
            if (count != body.Count)
                body = body.Take(count).ToArray();
        }
        if (body == null || body.Count == 0 || !body.Any(i => i.IP == selectionIp &&
                (IsSelection(i) || i.Mnemonic is Mnemonic.Comiss or Mnemonic.Ucomiss or Mnemonic.Comisd or Mnemonic.Ucomisd)))
            return false;
        var selection = body.Single(i => i.IP == selectionIp);
        var width = selection.Mnemonic is Mnemonic.Minss or Mnemonic.Maxss or Mnemonic.Comiss or Mnemonic.Ucomiss ? 32 : 64;
        var addresses = new HashSet<ulong>(body.Select(i => i.IP));
        var info = new InstructionInfoFactory();
        foreach (var instruction in body)
        {
            if (instruction.IsInvalid || instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None ||
                instruction.FlowControl is not (FlowControl.Next or FlowControl.Return or
                    FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch or FlowControl.Call))
                return false;
            if (instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch &&
                (instruction.Op0Kind != OpKind.NearBranch64 || !addresses.Contains(instruction.NearBranchTarget)) ||
                instruction.FlowControl == FlowControl.Call &&
                (instruction.Code != Code.Call_rel32_64 || noReturnNullCalls?.Contains(instruction.IP) != true))
                return false;
            var vectorRegisters = info.GetInfo(instruction).GetUsedRegisters()
                .Select(register => register.Register).Where(IsVectorRegister).ToArray();
            if (vectorRegisters.Length == 0)
            {
                // A general instruction need not name a vector register to restore
                // floating state. A bounded scalar-neutral set excludes such effects.
                if (!IsScalarNeutral(instruction)) return false;
                continue;
            }
            if (vectorRegisters.Any(register => !IsVolatileScalarRegister(register)))
                return false;
            switch (instruction.Mnemonic)
            {
                case Mnemonic.Minss: case Mnemonic.Maxss: case Mnemonic.Minsd: case Mnemonic.Maxsd:
                    if (!IsSelection(instruction) ||
                        (instruction.Mnemonic is Mnemonic.Minss or Mnemonic.Maxss ? 32 : 64) != width)
                        return false;
                    break;
                case Mnemonic.Movss: case Mnemonic.Movsd:
                case Mnemonic.Addss: case Mnemonic.Addsd: case Mnemonic.Subss: case Mnemonic.Subsd:
                case Mnemonic.Mulss: case Mnemonic.Mulsd: case Mnemonic.Divss: case Mnemonic.Divsd:
                case Mnemonic.Comiss: case Mnemonic.Comisd: case Mnemonic.Ucomiss: case Mnemonic.Ucomisd:
                    var single = instruction.Mnemonic is Mnemonic.Movss or Mnemonic.Addss or Mnemonic.Subss or
                        Mnemonic.Mulss or Mnemonic.Divss or Mnemonic.Comiss or Mnemonic.Ucomiss;
                    if ((single ? 32 : 64) != width) return false;
                    break;
                case Mnemonic.Movaps: case Mnemonic.Movapd: case Mnemonic.Movups: case Mnemonic.Movupd:
                    if (instruction.Op0Kind != OpKind.Register || instruction.Op1Kind != OpKind.Register)
                        return false;
                    break;
                case Mnemonic.Xorps: case Mnemonic.Xorpd: case Mnemonic.Pxor:
                    if (instruction.Op0Kind != OpKind.Register || instruction.Op1Kind != OpKind.Register ||
                        instruction.Op0Register != instruction.Op1Register)
                        return false;
                    break;
                default: return false;
            }
        }
        return body[^1].Code == Code.Retnq || noReturnNullCalls?.Contains(body[^1].IP) == true;
    }

    private static bool IsScalarNeutral(Instruction instruction)
    {
        for (var index = 0; index < instruction.OpCount; index++)
            if (instruction.GetOpKind(index) == OpKind.Register &&
                instruction.GetOpRegister(index).GetFullRegister() is not (>= Register.RAX and <= Register.R15))
                return false;
        return
        instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch or FlowControl.Call ||
        instruction.Mnemonic is Mnemonic.Nop or Mnemonic.Ret or Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movsx or
            Mnemonic.Movsxd or Mnemonic.Lea or Mnemonic.Push or Mnemonic.Pop or Mnemonic.Xchg or
            Mnemonic.Add or Mnemonic.Sub or Mnemonic.And or Mnemonic.Or or Mnemonic.Xor or
            Mnemonic.Test or Mnemonic.Cmp or Mnemonic.Inc or Mnemonic.Dec;
    }

    private static bool IsVolatileScalarRegister(Register register) => register is >= Register.XMM0 and <= Register.XMM5;
    private static bool IsVectorRegister(Register register) =>
        register is >= Register.XMM0 and <= Register.XMM31 or
            >= Register.YMM0 and <= Register.YMM31 or >= Register.ZMM0 and <= Register.ZMM31;
}
