using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using IsilRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Proves the complete frame-free virtual tail which passes literal false in
/// RDX and the paired vtable MethodInfo in R8. The selected slot must declare
/// one unchanged Boolean-taking virtual method on the caller's class.
/// </summary>
internal static class X64FalseBooleanVirtualTailProof
{
    internal readonly record struct Shape(ulong MethodPointerOffset, ulong End);
    internal sealed record Evidence(MethodAnalysisContext Target, int Slot);

    internal static Evidence? Find(MethodAnalysisContext method,
        IReadOnlyList<NativeInstruction> native)
    {
        method.EnsureRawBytes();
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            !X64VirtualTailDispatchProof.EligibleCaller(method) ||
            method.BaseMethod != null || method.Overrides.Count != 0 ||
            TryProveShape(native) is not { } shape ||
            method.UnderlyingPointer == 0 || native[0].IP != method.UnderlyingPointer ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings is not [var binding] || !ReferenceEquals(binding, method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) ||
            !X64VirtualTailDispatchProof.ClosedLeaf(method, native, shape.End, 19,
                pe, unwind) ||
            !X64VirtualTailDispatchProof.TryResolveOwnerSlot(method,
                shape.MethodPointerOffset, out var owner, out var target, out var slot) ||
            !EligibleTarget(target, owner, slot))
            return null;
        return new Evidence(target, slot);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        if (body.Count != 4 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix ||
                instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != NativeRegister.None) ||
            body[0].NextIP != body[1].IP || body[1].NextIP != body[2].IP ||
            body[2].NextIP != body[3].IP ||
            body[3].MemoryDisplacement64 > ulong.MaxValue - 8 ||
            !PointerLoad(body[0], NativeRegister.RAX, NativeRegister.RCX, 0) ||
            body[1].Code != Code.Xor_r32_rm32 ||
            body[1].FlowControl != FlowControl.Next || body[1].OpCount != 2 ||
            body[1].Op0Kind != OpKind.Register ||
            body[1].Op1Kind != OpKind.Register ||
            body[1].Op0Register != NativeRegister.EDX ||
            body[1].Op1Register != NativeRegister.EDX ||
            !PointerLoad(body[2], NativeRegister.R8, NativeRegister.RAX,
                body[3].MemoryDisplacement64 + 8) ||
            body[3].Code != Code.Jmp_rm64 ||
            body[3].FlowControl != FlowControl.IndirectBranch ||
            body[3].OpCount != 1 || body[3].Op0Kind != OpKind.Memory ||
            body[3].MemoryBase != NativeRegister.RAX ||
            body[3].MemoryIndex != NativeRegister.None ||
            body[3].MemoryIndexScale != 1 ||
            body[3].MemorySize.GetSize() != 8)
            return null;
        return new Shape(body[3].MemoryDisplacement64, body[3].NextIP);
    }

    private static bool EligibleTarget(MethodAnalysisContext target,
        TypeAnalysisContext owner, int slot)
    {
        var app = target.AppContext;
        if (!ReferenceEquals(target.DeclaringType, owner) ||
            target.Definition is not { GenericContainer: null, parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            definition.slot != slot ||
            definition.InternalParameterData is not [var rawParameter] ||
            target.Parameters is not [var parameter] ||
            !target.IsVirtual || target.IsStatic || target.IsFinal ||
            !target.IsNewSlot || target.IsAbstract ||
            target.BaseMethod != null || target.Overrides.Count != 0 ||
            target.Visibility != MethodAttributes.Public ||
            (target.Attributes & MethodAttributes.PinvokeImpl) != 0 ||
            target.Name != target.DefaultName ||
            target.GenericParameters.Count != 0 || target.OverrideReturnType != null ||
            target.Attributes != target.DefaultAttributes ||
            target.ImplAttributes != target.DefaultImplAttributes ||
            (target.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            !target.IsVoid ||
            !ReferenceEquals(target.ReturnType, app.SystemTypes.SystemVoidType) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(target) ||
            parameter.ParameterIndex != 0 ||
            !ReferenceEquals(parameter.DeclaringMethod, target) ||
            !ReferenceEquals(parameter.Definition, rawParameter) ||
            rawParameter.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            parameter.IsRef || parameter.OverrideParameterType != null ||
            !ReferenceEquals(parameter.ParameterType, app.SystemTypes.SystemBooleanType) ||
            !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) ||
            parameter.Name != parameter.DefaultName ||
            parameter.Attributes != ParameterAttributes.None ||
            parameter.Attributes != parameter.DefaultAttributes ||
            parameter.OverrideAttributes != null || parameter.UseOverrideDefaultValue)
            return false;

        var arguments = new X64CallingConventionResolver().ResolveForParameters(target);
        return arguments is [IsilRegister receiver, IsilRegister boolean,
                   IsilRegister methodInfo] &&
               receiver == new IsilRegister(null, "rcx") &&
               boolean == new IsilRegister(null, "rdx") &&
               methodInfo == new IsilRegister(null, "r8");
    }

    private static bool PointerLoad(NativeInstruction instruction,
        NativeRegister destination, NativeRegister source, ulong offset) =>
        instruction.Code == Code.Mov_r64_rm64 &&
        instruction.FlowControl == FlowControl.Next &&
        instruction.OpCount == 2 && instruction.Op0Kind == OpKind.Register &&
        instruction.Op0Register == destination &&
        instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == source &&
        instruction.MemoryIndex == NativeRegister.None &&
        instruction.MemoryIndexScale == 1 &&
        instruction.MemoryDisplacement64 == offset &&
        instruction.MemorySize.GetSize() == 8;
}
