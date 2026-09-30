using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64MetadataInitializationHelperProof
{
    /// <summary>
    /// Authenticates the Il2CppType usage arm independently of TypeInfo. Both
    /// routes must read the same registration and its pointer-sized type table;
    /// the Type route commits the selected Il2CppType without creating a class.
    /// </summary>
    internal static bool TryIdentifyType(ApplicationAnalysisContext app, PE pe,
        X64UnwindProof.Index unwind, ulong target)
    {
        try
        {
            if (!TryFindCore(app, pe, unwind, target, out var core))
                return false;

            var first = unwind.ClassifySpan(core, core + 1);
            if (first.Kind != X64UnwindProof.SpanKind.HandlerFree ||
                first.Start != core || first.RootStart != core)
                return false;
            return (first.End - core) switch
            {
                0x5d => TryIdentify(app, pe, unwind, target) &&
                        ProveTypeUsageArm(pe, unwind, core, alternate: false),
                0x37 => TryIdentifyAlternateCore(app, pe, unwind, target) &&
                        ProveTypeUsageArm(pe, unwind, core, alternate: true),
                _ => false,
            };
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool ProveTypeUsageArm(PE pe, X64UnwindProof.Index unwind,
        ulong core, bool alternate)
    {
        // The established core proof authenticates the decoder, TypeInfo arm,
        // resolver and commit. Extract their identities from those same bytes.
        var dispatchAddress = core + (alternate ? 0x37UL : 0x5dUL);
        var dispatch = Read(pe, unwind, dispatchAddress, alternate ? 15 : 8,
            alternate ? 0x3a : 0x23);
        if (dispatch == null || !SameRootInstructions(unwind, core, dispatch) ||
            !TableLoad(dispatch[alternate ? 12 : 1], out var tableRva) ||
            !TryReadTypeUsageSwitchArm(pe, unwind, tableRva, out var armAddress))
            return false;

        var typeInfoArmAddress = alternate ? dispatch[^1].NextIP : dispatch[4].IP;
        var typeInfoArm = alternate
            ? Read(pe, unwind, typeInfoArmAddress, 5, 0x13)
            : dispatch.Skip(4).ToArray();
        if (typeInfoArm == null || armAddress == typeInfoArmAddress ||
            !TablePointsTo(pe, unwind, tableRva, typeInfoArmAddress))
            return false;
        var join = typeInfoArm[^1].NearBranchTarget;
        var resolver = typeInfoArm[alternate ? 2 : 1].NearBranchTarget;
        var resolverBody = Read(pe, unwind, resolver, alternate ? 38 : 33,
            alternate ? 0x8d : 0x7c);
        if (resolverBody == null ||
            !RipLoad(resolverBody[alternate ? 21 : 16], Register.RAX,
                out var registration) || !WritablePointer(unwind, registration))
            return false;

        var arm = Read(pe, unwind, armAddress, alternate ? 4 : 5,
            alternate ? 0x14 : 0x16, allowChainedRegion: true);
        return arm != null && SameRootInstructions(unwind, core, arm) &&
               ProveTypeUsageArmShape(arm, alternate, join, out var armRegistration) &&
               armRegistration == registration;
    }

    private static bool TryReadTypeUsageSwitchArm(PE pe,
        X64UnwindProof.Index unwind, uint tableRva, out ulong arm)
    {
        arm = 0;
        const int tableLength = 28;
        if (tableRva > uint.MaxValue - tableLength ||
            unwind.ImageBase > ulong.MaxValue - tableRva - (tableLength - 1))
            return false;
        var address = unwind.ImageBase + tableRva;
        var first = pe.MapVirtualAddressToRaw(address, false);
        var image = pe.GetRawBinaryContent();
        if (first < 0 || first > image.Length - tableLength ||
            !unwind.IsUnaffectedByBaseRelocation(address, tableLength))
            return false;
        for (var offset = 0; offset < tableLength; offset++)
            if (!unwind.IsExecutableRva(tableRva + (uint)offset) ||
                unwind.IsWritableFileBackedRva(tableRva + (uint)offset) ||
                pe.MapVirtualAddressToRaw(address + (ulong)offset, false) != first + offset)
                return false;

        // The authenticated token decoder subtracts one from the usage tag.
        // TypeInfo occupies entry zero; Il2CppType occupies entry one.
        var armRva = BinaryPrimitives.ReadInt32LittleEndian(
            image.Slice((int)first + 4, 4));
        if (armRva < 0 || !unwind.IsExecutableRva((uint)armRva) ||
            unwind.ImageBase > ulong.MaxValue - (uint)armRva)
            return false;
        arm = unwind.ImageBase + (uint)armRva;
        return true;
    }

    internal static bool ProveTypeUsageArmShape(IReadOnlyList<Instruction> arm,
        bool alternate, ulong join, out ulong registration)
    {
        registration = 0;
        if (arm.Count != (alternate ? 4 : 5) ||
            arm.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None) ||
            arm.Where((instruction, index) => index > 0 &&
                instruction.IP != arm[index - 1].NextIP).Any() ||
            !RipLoad(arm[0], Register.RAX, out registration) ||
            !alternate && !Move(arm[1], Register.EDX, Register.ECX) ||
            !Load(arm[alternate ? 1 : 2], Register.RCX, Register.RAX, 0x38) ||
            !MemoryRegister(arm[alternate ? 2 : 3], Mnemonic.Mov, 1,
                Register.RCX, alternate ? Register.RDI : Register.RDX,
                8, 0, 8, Register.RBX) ||
            !Jump(arm[^1]) || arm[^1].NearBranchTarget != join)
            return false;
        return true;
    }
}
