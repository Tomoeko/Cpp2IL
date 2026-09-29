using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds a four-byte max_length read to a bounded native null guard. The array's
/// SZARRAY metadata and incoming/captured identity require a separate typed proof.
/// </summary>
internal static class X64ArrayLengthReadProof
{
    internal sealed record Site(ulong ReadAddress, NativeRegister ArrayRegister,
        NativeRegister ResultRegister, bool ComparisonCapture, ulong TestAddress,
        ulong BranchAddress, ulong NullCallAddress, ulong NullTarget,
        IReadOnlyList<NativeInstruction> Setup);

    internal static Site? Find(MethodAnalysisContext method, ulong readAddress)
    {
        if (!HasUnchangedMethod(method))
            return null;
        if (method.RawBytes.Length == 0)
            method.EnsureRawBytes();
        return X64NativeInstructionReader.ReadRootBody(method) is { } body &&
               TryProveSite(body, readAddress, Il2CppArrayUtils.GetLengthOffset(method.AppContext.Binary)) is { } site &&
               X86RuntimeNullThrowProof.TryIdentify(method.AppContext, site.NullTarget) != null
            ? site : null;
    }

    internal static bool HasUnchangedMethod(MethodAnalysisContext method)
    {
        var owner = method.DeclaringType;
        return X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) &&
               !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
               RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) &&
               method.AppContext.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) &&
               bindings.Count(candidate =>
                   ReferenceEquals(candidate, method)) == 1 &&
               method.Parameters.All(parameter => parameter.OverrideParameterType == null &&
                   parameter.Name == parameter.DefaultName && parameter.Attributes == parameter.DefaultAttributes) &&
               method.Name is not (".ctor" or ".cctor") && method.Name == method.DefaultName &&
               method.OverrideReturnType == null && ReferenceEquals(method.ReturnType, method.DefaultReturnType) &&
               method.Attributes == method.DefaultAttributes && method.ImplAttributes == method.DefaultImplAttributes &&
               (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
               (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                                         MethodImplAttributes.InternalCall)) == 0 &&
               method.GenericParameters.Count == 0 && !method.IsVirtual && owner != null &&
               NullCheckedCall.IsReferenceClass(owner) && owner.Name == owner.DefaultName &&
               owner.Namespace == owner.DefaultNamespace && owner.Definition is
               { GenericContainer: null, RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                   NumMods: 0, Byref: 0, Pinned: 0 } };
    }

    internal static Site? TryProveSite(IReadOnlyList<NativeInstruction> body, ulong readAddress,
        long lengthOffset = 0x18)
    {
        if (body.Count is < 4 or > 512 || lengthOffset <= 0 ||
            body.Any(instruction => instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64))
            return null;
        for (var index = 1; index < body.Count; index++)
            if (body[index - 1].NextIP != body[index].IP)
                return null;
        var matches = body.Select((instruction, index) => (instruction, index))
            .Where(pair => pair.instruction.IP == readAddress).ToArray();
        if (matches is not [var match] || match.index < 2)
            return null;
        var read = match.instruction;
        var guardIndex = match.index - 1;
        while (guardIndex >= 0 && match.index - guardIndex <= 5 && IsSetup(body[guardIndex]))
            guardIndex--;
        if (guardIndex < 1 || match.index - guardIndex > 5)
            return null;
        var test = body[guardIndex - 1];
        var branch = body[guardIndex];
        var setup = body.Skip(guardIndex + 1).Take(match.index - guardIndex - 1).ToArray();
        if (setup.Count(instruction => instruction.Op1Kind == OpKind.Memory) > 1)
            return null;
        var comparison = read.Code == Code.Cmp_r32_rm32;
        if (!comparison && read.Code != Code.Mov_r32_rm32 ||
            read.Op0Kind != OpKind.Register || read.Op0Register.GetSize() != 4 ||
            read.Op1Kind != OpKind.Memory || read.MemorySize.GetSize() != 4 ||
            read.MemoryBase is < NativeRegister.RAX or > NativeRegister.R15 ||
            read.MemoryBase == NativeRegister.RSP || read.MemoryIndex != NativeRegister.None ||
            read.MemoryIndexScale != 1 || read.MemoryDisplacement64 != (ulong)lengthOffset ||
            setup.Any(instruction => instruction.Op0Register.GetFullRegister() == read.MemoryBase) ||
            !NoPrefixes(read) || !NoPrefixes(test) || !NoPrefixes(branch) ||
            test.Code != Code.Test_rm64_r64 || test.Op0Kind != OpKind.Register ||
            test.Op1Kind != OpKind.Register || test.Op0Register != read.MemoryBase ||
            test.Op1Register != read.MemoryBase ||
            branch.Code is not (Code.Je_rel8_64 or Code.Je_rel32_64) ||
            branch.Op0Kind != OpKind.NearBranch64 || branch.NearBranchTarget <= read.IP ||
            // An entry at the branch could consume other flags; an entry at the
            // read could bypass the native null check. Entry at TEST is safe.
            body.Any(instruction => instruction.Op0Kind == OpKind.NearBranch64 &&
                instruction.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch &&
                instruction.NearBranchTarget >= branch.IP && instruction.NearBranchTarget <= read.IP))
            return null;
        var nullArm = body.Where(instruction => instruction.IP == branch.NearBranchTarget).ToArray();
        return nullArm is [{ Code: Code.Call_rel32_64, Op0Kind: OpKind.NearBranch64 } call] && NoPrefixes(call)
            ? new Site(read.IP, read.MemoryBase, comparison ? NativeRegister.None : read.Op0Register.GetFullRegister(),
                comparison, test.IP, branch.IP, call.IP, call.NearBranchTarget, setup)
            : null;
    }

    internal static bool SameSite(Site left, Site right) =>
        (left with { Setup = right.Setup }) == right && left.Setup.SequenceEqual(right.Setup);

    // Only bounded native register setup and one candidate Int32 owner-field
    // read may separate the guard and Length. The typed binder authenticates
    // that read; a separate null-arm Length probe retains its original timing.
    private static bool IsSetup(NativeInstruction instruction)
    {
        if (!NoPrefixes(instruction) || instruction.Op0Kind != OpKind.Register ||
            instruction.Op0Register.GetSize() != 4 || instruction.Op0Register.GetFullRegister() == NativeRegister.RSP)
            return false;
        if (instruction.Code is Code.Xor_r32_rm32 or Code.Xor_rm32_r32 && instruction.Op1Kind == OpKind.Register)
            return instruction.Op0Register == instruction.Op1Register;
        if (instruction.Code is Code.Mov_r32_rm32 or Code.Mov_rm32_r32 && instruction.Op1Kind == OpKind.Register)
            return instruction.Op1Register.GetSize() == 4 && instruction.Op1Register.GetFullRegister() != NativeRegister.RSP;
        return instruction.Code == Code.Mov_r32_rm32 && instruction.Op1Kind == OpKind.Memory &&
               instruction.MemoryBase == NativeRegister.RCX && instruction.MemoryIndex == NativeRegister.None &&
               instruction.MemoryIndexScale == 1 && instruction.MemorySize.GetSize() == 4 &&
               instruction.MemoryDisplacement64 is >= 16 and <= 4096;
    }

    private static bool NoPrefixes(NativeInstruction instruction) =>
        !instruction.HasLockPrefix && !instruction.HasRepPrefix && !instruction.HasRepnePrefix &&
        instruction.SegmentPrefix == NativeRegister.None;
}
