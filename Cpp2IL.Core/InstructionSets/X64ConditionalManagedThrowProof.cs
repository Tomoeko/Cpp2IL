using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves a complete Boolean-controlled return-or-throw body. Both the normal
/// Int32 addition and the exceptional allocation, construction and raise path
/// must be established before replacing the native instructions.
/// </summary>
internal static class X64ConditionalManagedThrowProof
{
    private static readonly byte[] LateSavedRbxFrame =
        [0x1C, 0x34, 0x04, 0x00, 0x04, 0x42];
    private static readonly X64CallingConventionResolver CallingConventions = new();

    internal sealed record Shape(ulong TypeInfoSlot, ulong MethodDefSlot,
        ulong MetadataInitializer, ulong Allocator, ulong NullGuard,
        ulong Constructor, ulong Raiser);

    internal static List<ISIL.Instruction>? TryLift(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> decoded)
    {
        if (Find(method, decoded) is not { } evidence)
            return null;

        var predicate = new ISIL.Register(null, "conditional_throw_predicate");
        var result = new ISIL.Register(null, "conditional_throw_result");
        var exception = new ISIL.Register(null, "conditional_throw_exception");
        var instructions = new List<ISIL.Instruction>
        {
            new(0, ISIL.OpCode.CheckNotEqual, predicate,
                new ISIL.Register(null, "rcx"), new ISIL.Immediate(0)),
            new(1, ISIL.OpCode.ConditionalJump, new ISIL.Immediate(0), predicate),
            new(2, ISIL.OpCode.Add, result, new ISIL.Register(null, "rdx"),
                new ISIL.Immediate(1)) { IntegerBitWidth = 32 },
            new(3, ISIL.OpCode.Return, result),
            new(4, ISIL.OpCode.Newobj, exception, evidence.ExceptionType),
            new(5, ISIL.OpCode.CallVoid, evidence.Constructor, exception,
                new ISIL.Immediate(0)),
            new(6, ISIL.OpCode.Throw, exception),
        };
        instructions[1].SetOperand(0, instructions[4]);
        return instructions;
    }

    internal static X64TerminalManagedThrowProof.Evidence? Find(
        MethodAnalysisContext method, IReadOnlyList<NativeInstruction> decoded)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
                app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                !HasCallerIdentity(method))
                return null;

            if (method.RawBytes.Length == 0)
                method.EnsureRawBytes();
            if (X64NativeInstructionReader.ReadRootBody(method) is not { } body ||
                !body.SequenceEqual(decoded.Take(body.Length)) ||
                TryProveShape(body) is not { } shape ||
                !unwind.MatchesUnwind(body[0].IP, body[^1].NextIP, 0x1C, 0,
                    LateSavedRbxFrame) ||
                body[^1].NextIP > ulong.MaxValue - 15 ||
                !X64NativePaddingProof.HasInt3Padding(pe, body[^1].NextIP,
                    (body[^1].NextIP + 15) & ~15UL) ||
                app.MethodsByAddress.Keys.Any(address =>
                    address >= body[^1].NextIP &&
                    address < ((body[^1].NextIP + 15) & ~15UL)) ||
                X86CallerExceptionRegionProof.Check(method, body,
                    new HashSet<ulong> { body[21].IP }) != null)
                return null;

            return BindProvedShape(method, shape);
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    // Find authenticates the entire root unwind region before binding this
    // shape. Keeping binding separate protects helper and metadata negatives
    // without patching or copying a player binary.
    internal static X64TerminalManagedThrowProof.Evidence? BindProvedShape(
        MethodAnalysisContext method, Shape shape)
    {
        var app = method.AppContext;
        if (!HasCallerIdentity(method) || app.Binary is not PE pe ||
            X64UnwindProof.ForApplication(app) is not { } unwind ||
            shape.MetadataInitializer != app.GetOrCreateKeyFunctionAddresses()
                .il2cpp_codegen_initialize_runtime_metadata ||
            !X64MetadataInitializationHelperProof.TryIdentifyMethodDefArm(
                app, pe, unwind, shape.MetadataInitializer) ||
            !X64IteratorAllocatorProof.IsAllocator(app, shape.Allocator) ||
            !X64TerminalManagedThrowProof.ProveNullCheck(app, pe, unwind,
                shape.NullGuard) ||
            !X64TerminalManagedThrowProof.ProveRaiseWrapper(app, pe, unwind,
                shape.Raiser))
            return null;

        return X64TerminalManagedThrowProof.BindMetadata(method, pe, unwind,
            shape.TypeInfoSlot, shape.MethodDefSlot, shape.Constructor);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 23 || body[0].IP == 0 ||
            body[^1].NextIP - body[0].IP != 90 ||
            body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body.Where((instruction, index) => index > 0 &&
                instruction.IP != body[index - 1].NextIP).Any() ||
            !Stack(body[0], Code.Sub_rm64_imm8) ||
            !Registers(body[1], Code.Test_rm8_r8,
                NativeRegister.CL, NativeRegister.CL) ||
            !Branch(body[2], Code.Jne_rel8_64, body[6].IP) ||
            !AddOne(body[3]) || !Stack(body[4], Code.Add_rm64_imm8) ||
            body[5].Code != Code.Retnq || body[5].OpCount != 0 ||
            !RipAddress(body[6]) || !SavedRbx(body[7], restore: false) ||
            body[7].NextIP - body[0].IP != 0x1C ||
            !DirectCall(body[8]) ||
            !Registers(body[9], Code.Mov_r64_rm64,
                NativeRegister.RCX, NativeRegister.RAX) ||
            !DirectCall(body[10]) ||
            !Registers(body[11], Code.Mov_r64_rm64,
                NativeRegister.RCX, NativeRegister.RAX) ||
            !Registers(body[12], Code.Mov_r64_rm64,
                NativeRegister.RBX, NativeRegister.RAX) ||
            !DirectCall(body[13]) ||
            !Registers(body[14], Code.Xor_r32_rm32,
                NativeRegister.EDX, NativeRegister.EDX) ||
            !Registers(body[15], Code.Mov_r64_rm64,
                NativeRegister.RCX, NativeRegister.RBX) ||
            !DirectCall(body[16]) || !RipAddress(body[17]) ||
            !DirectCall(body[18]) ||
            body[18].NearBranchTarget != body[8].NearBranchTarget ||
            !Registers(body[19], Code.Mov_r64_rm64,
                NativeRegister.RDX, NativeRegister.RAX) ||
            !Registers(body[20], Code.Mov_r64_rm64,
                NativeRegister.RCX, NativeRegister.RBX) ||
            !DirectCall(body[21]) || !SavedRbx(body[22], restore: true))
            return null;

        // MSVC saves RBX only on the throw arm, and describes that late save
        // in the root unwind prolog. The final RBX restore is authenticated
        // even though the proved raise call makes it unreachable.
        return new Shape(body[6].IPRelativeMemoryAddress,
            body[17].IPRelativeMemoryAddress, body[8].NearBranchTarget,
            body[10].NearBranchTarget, body[13].NearBranchTarget,
            body[16].NearBranchTarget, body[21].NearBranchTarget);
    }

    private static bool Registers(NativeInstruction instruction, Code code,
        NativeRegister destination, NativeRegister source) =>
        instruction.Code == code && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Register &&
        instruction.Op1Register == source;

    private static bool AddOne(NativeInstruction instruction) =>
        instruction.Code == Code.Lea_r32_m && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.EAX &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RDX &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryDisplacement64 == 1;

    private static bool SavedRbx(NativeInstruction instruction, bool restore)
    {
        var memory = restore ? 1 : 0;
        var register = 1 - memory;
        return instruction.Code == (restore ? Code.Mov_r64_rm64 : Code.Mov_rm64_r64) &&
            instruction.OpCount == 2 &&
            instruction.GetOpKind(register) == OpKind.Register &&
            instruction.GetOpRegister(register) == NativeRegister.RBX &&
            instruction.GetOpKind(memory) == OpKind.Memory &&
            instruction.MemoryBase == NativeRegister.RSP &&
            instruction.MemoryIndex == NativeRegister.None &&
            instruction.MemoryDisplacement64 == 0x20 &&
            instruction.MemorySize.GetSize() == 8;
    }

    private static bool Stack(NativeInstruction instruction, Code code) =>
        instruction.Code == code && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RSP &&
        instruction.Op1Kind == OpKind.Immediate8to64 &&
        instruction.GetImmediate(1) == 0x28;

    private static bool RipAddress(NativeInstruction instruction) =>
        instruction.Code == Code.Lea_r64_m && instruction.OpCount == 2 &&
        instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == NativeRegister.RCX &&
        instruction.Op1Kind == OpKind.Memory &&
        instruction.MemoryBase == NativeRegister.RIP &&
        instruction.MemoryIndex == NativeRegister.None;

    private static bool DirectCall(NativeInstruction instruction) =>
        instruction.Code == Code.Call_rel32_64 && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget != 0;

    private static bool Branch(NativeInstruction instruction, Code code,
        ulong target) =>
        instruction.Code == code && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.NearBranch64 &&
        instruction.NearBranchTarget == target;

    private static bool HasCallerIdentity(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        return method.DeclaringType is { } owner &&
            X64MetadataStaticGetterProof.OrdinaryOwner(owner) &&
            ReferenceEquals(owner.BaseType, app.SystemTypes.SystemObjectType) &&
            method.Definition is { GenericContainer: null, parameterCount: 2,
                InternalParameterData: [var rawFail, var rawValue],
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
            ReferenceEquals(definition.DeclaringType, owner.Definition) &&
            rawFail.RawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 } &&
            rawValue.RawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 } &&
            method.Parameters is [var fail, var value] &&
            Parameter(fail, rawFail, method, 0, app.SystemTypes.SystemBooleanType) &&
            Parameter(value, rawValue, method, 1, app.SystemTypes.SystemInt32Type) &&
            ReferenceEquals(method.ReturnType, app.SystemTypes.SystemInt32Type) &&
            ReferenceEquals(method.DefaultReturnType, method.ReturnType) &&
            method.IsStatic && !method.IsVirtual && !method.IsVoid &&
            method.Name is not (".ctor" or ".cctor") &&
            method.Name == method.DefaultName && method.GenericParameters.Count == 0 &&
            method.OverrideReturnType == null &&
            method.Attributes == method.DefaultAttributes &&
            method.ImplAttributes == method.DefaultImplAttributes &&
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl |
                MethodAttributes.SpecialName)) == 0 &&
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) == 0 &&
            !RuntimeNullGuardCoalescer.HasOutputOptions(method) &&
            RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) &&
            !CallingConventions.ReturnsViaHiddenBuffer(method) &&
            CallingConventions.ReturnRegister(method).Name == "rax" &&
            CallingConventions.ResolveForManaged(method) is
                [ISIL.Register { Name: "rcx" }, ISIL.Register { Name: "rdx" },
                    ISIL.Register { Name: "r8" }];
    }

    private static bool Parameter(ParameterAnalysisContext parameter,
        Il2CppParameterDefinition raw, MethodAnalysisContext method, int index,
        TypeAnalysisContext type) =>
        ReferenceEquals(parameter.Definition, raw) &&
        ReferenceEquals(parameter.DeclaringMethod, method) &&
        parameter.ParameterIndex == index && !parameter.IsRef &&
        parameter.Attributes == parameter.DefaultAttributes &&
        parameter.Name == parameter.DefaultName &&
        parameter.OverrideParameterType == null &&
        parameter.OverrideAttributes == null && !parameter.UseOverrideDefaultValue &&
        ReferenceEquals(parameter.ParameterType, type);
}
