using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Removes only Windows x64 ABI preservation traffic for XMM6-XMM15. A paired
/// 128-bit save and restore says nothing about the managed type or lane contents;
/// all intervening vector operations still require their own semantic proofs.
/// </summary>
internal static class X86NonvolatileXmmStackProof
{
    internal static HashSet<ulong> Find(MethodAnalysisContext method, IReadOnlyList<Instruction> native)
    {
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
            method.AppContext.Binary is not PE ||
            !native.Any(IsStackXmmSave) ||
            X64UnwindProof.ForApplication(method.AppContext) is not { } unwind)
            return [];
        return Prove(native, method.UnderlyingPointer, unwind,
            instruction => SafeDirectCall(method.AppContext, instruction));
    }

    // This overload accepts an already parsed, file-backed PE unwind index. It is
    // also used by focused regressions with a synthetic PE section and .pdata row.
    internal static HashSet<ulong> Prove(IReadOnlyList<Instruction> native, ulong entry,
        X64UnwindProof.Index unwind, Func<Instruction, bool> safeCall)
    {
        var result = new HashSet<ulong>();
        if (native.Count is < 5 or > 4096 || native[0].IP != entry ||
            native[^1].Code != Code.Retnq || native[^1].OpCount != 0 ||
            native.Where((instruction, index) => index > 0 &&
                instruction.IP != native[index - 1].NextIP).Any() ||
            native.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.FlowControl is not (FlowControl.Next or FlowControl.Call or FlowControl.Return)) ||
            native.Take(native.Count - 1).Any(instruction => instruction.FlowControl == FlowControl.Return))
            return result;

        var region = unwind.ClassifySpan(entry, native[^1].NextIP);
        if (region.Kind != X64UnwindProof.SpanKind.HandlerFree ||
            region.Start != entry || region.RootStart != entry ||
            region.End != native[^1].NextIP)
            return result;

        var saves = native.Select((instruction, index) => (instruction, index))
            .Where(item => IsStackXmmSave(item.instruction)).ToArray();
        var restores = native.Select((instruction, index) => (instruction, index))
            .Where(item => IsStackXmmRestore(item.instruction)).ToArray();
        if (saves.Length is < 1 or > 10 || restores.Length != saves.Length ||
            saves.Select(item => item.instruction.Op1Register).Distinct().Count() != saves.Length ||
            restores.Select(item => item.instruction.Op0Register).Distinct().Count() != restores.Length ||
            saves[^1].index >= restores[0].index)
            return result;

        var pairs = new List<Pair>(saves.Length);
        foreach (var (save, saveIndex) in saves)
        {
            var matches = restores.Where(item => item.instruction.Op0Register == save.Op1Register &&
                item.instruction.MemoryDisplacement64 == save.MemoryDisplacement64).ToArray();
            if (matches is not [var restore] || restore.index <= saveIndex ||
                save.MemoryDisplacement64 > uint.MaxValue ||
                save.MemoryDisplacement64 % 16 != 0 ||
                save.MemoryDisplacement64 < 0x20 ||
                !OrdinaryVectorMemoryInstruction(save) ||
                !OrdinaryVectorMemoryInstruction(restore.instruction))
                return result;
            pairs.Add(new(saveIndex, restore.index, save.Op1Register,
                checked((uint)save.MemoryDisplacement64)));
        }

        var unwindSaves = pairs.Select(pair => new X64UnwindProof.Xmm128Save(
            native[pair.SaveIndex].NextIP, (int)pair.Register - (int)Register.XMM0,
            pair.Displacement)).ToArray();
        if (!StableExclusiveStack(native, pairs, safeCall, out var frameSize) ||
            !unwind.MatchesXmm128Saves(entry, unwindSaves, frameSize))
            return result;

        foreach (var pair in pairs)
        {
            result.Add(native[pair.SaveIndex].IP);
            result.Add(native[pair.RestoreIndex].IP);
        }
        return result;
    }

    private readonly record struct Pair(int SaveIndex, int RestoreIndex,
        Register Register, uint Displacement);

    private static bool IsStackXmmSave(Instruction instruction) =>
        instruction.Mnemonic == Mnemonic.Movaps &&
        instruction.Op0Kind == OpKind.Memory && instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register is >= Register.XMM6 and <= Register.XMM15 &&
        instruction.MemoryBase == Register.RSP && instruction.MemoryIndex == Register.None &&
        instruction.MemorySize.GetSize() == 16;

    private static bool IsStackXmmRestore(Instruction instruction) =>
        instruction.Mnemonic == Mnemonic.Movaps &&
        instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Memory &&
        instruction.Op0Register is >= Register.XMM6 and <= Register.XMM15 &&
        instruction.MemoryBase == Register.RSP && instruction.MemoryIndex == Register.None &&
        instruction.MemorySize.GetSize() == 16;

    private static bool OrdinaryVectorMemoryInstruction(Instruction instruction) =>
        instruction.CodeSize == CodeSize.Code64 && !instruction.IsInvalid &&
        !instruction.HasLockPrefix && !instruction.HasRepPrefix &&
        !instruction.HasRepnePrefix && instruction.SegmentPrefix == Register.None &&
        instruction.MemoryDisplSize <= 4;

    private static bool StableExclusiveStack(IReadOnlyList<Instruction> native,
        IReadOnlyList<Pair> pairs, Func<Instruction, bool> safeCall,
        out uint frameSize)
    {
        frameSize = 0;
        var firstSave = pairs.Min(pair => pair.SaveIndex);
        var finalSave = pairs.Max(pair => pair.SaveIndex);
        var firstRestore = pairs.Min(pair => pair.RestoreIndex);
        var lastRestore = pairs.Max(pair => pair.RestoreIndex);
        var byIndex = new Dictionary<int, Pair>();
        var registerInfo = new InstructionInfoFactory();
        foreach (var pair in pairs)
        {
            byIndex.Add(pair.SaveIndex, pair);
            byIndex.Add(pair.RestoreIndex, pair);
        }

        var deltas = new long[native.Count];
        var delta = 0L;
        for (var index = 0; index < native.Count; index++)
        {
            var instruction = native[index];
            deltas[index] = delta;
            if (instruction.Mnemonic == Mnemonic.Lea &&
                instruction.MemoryBase is Register.RSP or Register.ESP or Register.RBP or Register.EBP)
                return false; // A computed stack address can escape through a later operand or call.
            if (instruction.MemoryBase is Register.RBP or Register.EBP or Register.ESP ||
                instruction.MemoryBase == Register.RSP && instruction.MemoryIndex != Register.None)
                return false;
            if (instruction.FlowControl == FlowControl.Call &&
                (index <= finalSave || index >= firstRestore || !safeCall(instruction)))
                return false;

            if (!TryAdvanceStack(instruction, ref delta))
                return false;
            if (instruction.FlowControl is not (FlowControl.Call or FlowControl.Return) &&
                instruction.Mnemonic is not (Mnemonic.Push or Mnemonic.Pop) &&
                !(instruction.Op0Kind == OpKind.Register && instruction.Op0Register == Register.RSP &&
                    instruction.Mnemonic is Mnemonic.Add or Mnemonic.Sub) &&
                registerInfo.GetInfo(instruction).GetUsedRegisters().Any(used =>
                    used.Register.GetFullRegister() == Register.RSP &&
                    used.Access is OpAccess.Write or OpAccess.ReadWrite or
                        OpAccess.CondWrite or OpAccess.ReadCondWrite))
                return false;
            if (index >= firstSave && index < lastRestore && delta != deltas[index])
                return false;
            if (delta is < -0x100000 or > 0x100000)
                return false;
        }
        if (delta != 0 || pairs.Any(pair =>
                deltas[pair.SaveIndex] != deltas[pair.RestoreIndex] ||
                deltas[pair.SaveIndex] != deltas[firstSave]))
            return false;

        var slots = pairs.Select(pair => (Pair: pair,
            Start: deltas[pair.SaveIndex] + pair.Displacement)).ToArray();
        if (deltas[firstSave] >= 0 || slots.Any(slot =>
                slot.Start < deltas[firstSave] || slot.Start + 16 > 0))
            return false;
        if (slots.Any(slot => slots.Any(other => slot.Pair != other.Pair &&
                Overlaps(slot.Start, 16, other.Start, 16))))
            return false;

        for (var index = 0; index < native.Count; index++)
        {
            var instruction = native[index];
            if (byIndex.ContainsKey(index))
                continue;
            var info = registerInfo.GetInfo(instruction);
            foreach (var pair in pairs)
                if (index < pair.SaveIndex || index > pair.RestoreIndex)
                    if (info.GetUsedRegisters().Any(used => SameVectorRegister(used.Register, pair.Register)))
                        return false;

            if (instruction.MemoryBase == Register.RSP)
            {
                var size = instruction.MemorySize.GetSize();
                if (size <= 0 || instruction.MemoryDisplacement64 > long.MaxValue)
                    return false;
                var address = deltas[index] + (long)instruction.MemoryDisplacement64;
                if (slots.Any(slot => Overlaps(address, size, slot.Start, 16)))
                    return false;
            }
            if (instruction.Mnemonic == Mnemonic.Push &&
                slots.Any(slot => Overlaps(deltas[index] - 8, 8, slot.Start, 16)) ||
                instruction.Mnemonic == Mnemonic.Pop &&
                slots.Any(slot => Overlaps(deltas[index], 8, slot.Start, 16)) ||
                instruction.FlowControl == FlowControl.Call &&
                slots.Any(slot => Overlaps(deltas[index] - 8, 0x28, slot.Start, 16)))
                return false;
        }
        frameSize = checked((uint)-deltas[firstSave]);
        return true;
    }

    private static bool Overlaps(long first, long firstLength, long second, long secondLength) =>
        first < second + secondLength && second < first + firstLength;

    private static bool SameVectorRegister(Register used, Register xmm)
    {
        var number = (int)xmm - (int)Register.XMM0;
        return used == xmm || used == Register.YMM0 + number || used == Register.ZMM0 + number;
    }

    private static bool TryAdvanceStack(Instruction instruction, ref long delta)
    {
        if (instruction.Mnemonic is Mnemonic.Push or Mnemonic.Pop)
        {
            if (instruction.Op0Kind != OpKind.Register || instruction.Op0Register == Register.RSP ||
                instruction.Op0Register.GetSize() != 8)
                return false;
            delta += instruction.Mnemonic == Mnemonic.Push ? -8 : 8;
            return true;
        }
        if (instruction.Op0Kind == OpKind.Register && instruction.Op0Register == Register.RSP &&
            instruction.Mnemonic is Mnemonic.Add or Mnemonic.Sub &&
            instruction.Op1Kind.IsImmediate() && instruction.GetImmediate(1) <= 0x100000)
        {
            var amount = (long)instruction.GetImmediate(1);
            delta += instruction.Mnemonic == Mnemonic.Sub ? -amount : amount;
            return true;
        }
        for (var operand = 0; operand < instruction.OpCount; operand++)
            if (instruction.GetOpKind(operand) == OpKind.Register &&
                instruction.GetOpRegister(operand) is Register.RSP or Register.ESP)
                return false;
        return true;
    }

    private static bool SafeDirectCall(ApplicationAnalysisContext app, Instruction instruction)
    {
        if (instruction.Code != Code.Call_rel32_64 || instruction.Op0Kind != OpKind.NearBranch64 ||
            !app.MethodsByAddress.TryGetValue(instruction.NearBranchTarget, out var bindings) ||
            bindings is not [var target] ||
            target is ConcreteGenericMethodAnalysisContext ||
            target.DeclaringType is not { IsGenericInstance: false, GenericParameters.Count: 0 } ||
            target.GenericParameters.Count != 0 || target.Name != target.DefaultName ||
            target.OverrideReturnType != null ||
            target.Parameters.Count + (target.IsStatic ? 0 : 1) + 2 > 4 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target))
            return false;
        return target.Parameters.All(parameter => parameter.OverrideParameterType == null &&
            parameter.OverrideAttributes == null && !parameter.UseOverrideDefaultValue);
    }
}
