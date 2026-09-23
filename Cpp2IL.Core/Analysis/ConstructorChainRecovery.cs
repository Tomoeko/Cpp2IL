using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Restores the mandatory immediate-base constructor call when IL2CPP folds
/// empty constructor chains into one shared tail thunk to Object..ctor.
/// </summary>
internal static class ConstructorChainRecovery
{
    public static int Run(MethodAnalysisContext method)
    {
        var app = method.AppContext;
        if (app.Binary is not PE { PointerSizeBytes: 8 } ||
            app.Binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
            app.UnityVersion.ToString() != "2021.3.35f1" ||
            method.Name != ".ctor" || method.IsStatic || method.IsVirtual ||
            method.Name != method.DefaultName || method.Parameters.Count != 0 ||
            method.OverrideReturnType != null || !method.IsVoid ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            method.GenericParameters.Count != 0 ||
            method.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } ||
            method.DeclaringType is not { } owner ||
            owner.Definition?.GenericContainer != null || owner.IsGenericInstance ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            owner.BaseType is not { } baseType ||
            ReferenceEquals(baseType, app.SystemTypes.SystemObjectType) ||
            baseType.Definition?.GenericContainer != null || baseType.IsGenericInstance ||
            !NullCheckedCall.IsReferenceClass(baseType) ||
            method.UnderlyingPointer == 0 ||
            !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var aliases) ||
            !aliases.Contains(method))
            return 0;

        var baseConstructors = baseType.Methods.Where(candidate =>
            candidate.Name == ".ctor" && !candidate.IsStatic && !candidate.IsVirtual &&
            candidate.Parameters.Count == 0 && candidate.IsVoid &&
            candidate.OverrideReturnType == null &&
            candidate.Attributes == candidate.DefaultAttributes &&
            candidate.ImplAttributes == candidate.DefaultImplAttributes &&
            candidate.GenericParameters.Count == 0 &&
            candidate.Definition is { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } &&
            candidate.UnderlyingPointer == method.UnderlyingPointer &&
            aliases.Contains(candidate)).ToArray();
        if (baseConstructors is not [{ } baseConstructor])
            return 0;

        var active = method.ControlFlowGraph!.Instructions
            .Where(instruction => instruction.OpCode != OpCode.Nop).ToArray();
        if (active is not
            [{ OpCode: OpCode.CallVoid, IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct,
                Operands: [MethodAnalysisContext objectConstructor, LocalVariable receiver] } call,
             { OpCode: OpCode.Return, Operands.Count: 0 }] ||
            !receiver.IsThis || !method.ParameterLocals.Contains(receiver) ||
            !ReferenceEquals(receiver.Type, owner) ||
            objectConstructor.Name != ".ctor" || objectConstructor.IsStatic ||
            !ReferenceEquals(objectConstructor.DeclaringType, app.SystemTypes.SystemObjectType) ||
            objectConstructor.Parameters.Count != 0 || objectConstructor.UnderlyingPointer == 0)
            return 0;

        var body = X86Utils.Iterate(method).ToArray();
        if (!TryProveShape(body, method.UnderlyingPointer, method.RawBytes.Length,
                objectConstructor.UnderlyingPointer) ||
            X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null)
            return 0;

        call.SetOperand(0, baseConstructor);
        return 1;
    }

    internal static bool TryProveShape(IReadOnlyList<Iced.Intel.Instruction> body,
        ulong methodStart, int byteLength, ulong objectConstructorPointer)
    {
        if (body is not [var clear, var jump] || byteLength <= 0 ||
            clear.IP != methodStart || jump.IP != clear.NextIP ||
            jump.NextIP - methodStart != (ulong)byteLength ||
            objectConstructorPointer == 0 ||
            !X86InstructionSet.TargetsOutsideMethod(objectConstructorPointer,
                methodStart, byteLength))
            return false;
        foreach (var instruction in body)
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != NativeRegister.None)
                return false;
        return clear.Mnemonic == Mnemonic.Xor &&
               clear.Op0Kind == OpKind.Register && clear.Op0Register == NativeRegister.EDX &&
               clear.Op1Kind == OpKind.Register && clear.Op1Register == NativeRegister.EDX &&
               jump.Code == Code.Jmp_rel32_64 && jump.Op0Kind == OpKind.NearBranch64 &&
               jump.NearBranchTarget == objectConstructorPointer;
    }
}
