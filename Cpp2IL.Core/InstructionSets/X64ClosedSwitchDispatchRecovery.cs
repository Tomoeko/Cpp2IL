using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds a closed native table to an unchanged managed Int32 selector. The
/// dispatch prefix changes no memory and its scratch registers and flags must
/// be dead at every case and default entry before it can be replaced by direct
/// managed branches. The case bodies remain owned by ordinary instruction
/// lifting and typed IL validation.
/// </summary>
internal static class X64ClosedSwitchDispatchRecovery
{
    internal static X64ClosedSwitchTableProof.Evidence? Find(MethodAnalysisContext method)
    {
        var evidence = X64ClosedSwitchTableProof.Find(method);
        return evidence != null && BoundInt32Selector(method, evidence) &&
               DispatchValuesDeadAtTargets(evidence) ? evidence : null;
    }

    internal static void AppendDispatch(X64ClosedSwitchTableProof.Evidence evidence,
        List<ISIL.Instruction> instructions, List<ulong> addresses)
    {
        var selector = new ISIL.Register(null, "rcx");
        var predicate = new ISIL.Register(null, "closed_switch_match");
        for (var i = 0; i < evidence.CaseTargets.Count; i++)
        {
            Add(ISIL.OpCode.CheckEqual, predicate, selector, new ISIL.Immediate(i))
                .IntegerBitWidth = 32;
            Add(ISIL.OpCode.ConditionalJump,
                new ISIL.Immediate(unchecked((long)evidence.CaseTargets[i])), predicate);
        }
        // An Int32 outside 0..N-1, including every negative selector, reaches
        // the same default arm as the proven unsigned JA guard.
        Add(ISIL.OpCode.Jump,
            new ISIL.Immediate(unchecked((long)evidence.DefaultTarget)));

        ISIL.Instruction Add(ISIL.OpCode opcode, params ISIL.IOperand[] operands)
        {
            var lifted = new ISIL.Instruction(instructions.Count, opcode,
                operands.ToList())
            {
                NativeAddress = evidence.Dispatch,
            };
            instructions.Add(lifted);
            addresses.Add(evidence.Dispatch);
            return lifted;
        }
    }

    internal static bool BoundInt32Selector(MethodAnalysisContext method,
        X64ClosedSwitchTableProof.Evidence evidence)
    {
        var app = method.AppContext;
        if (method.DeclaringType is not { Definition: { GenericContainer: null } owner } ||
            method.Definition is not { GenericContainer: null, parameterCount: > 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner) ||
            method.Parameters.Count == 0 ||
            definition.InternalParameterData is not { Length: > 0 } rawParameters ||
            !method.IsStatic || method.IsVirtual ||
            method.Name is ".ctor" or ".cctor" ||
            method.Name != method.DefaultName ||
            method.GenericParameters.Count != 0 || method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                     MethodImplAttributes.ManagedMask |
                                     MethodImplAttributes.InternalCall)) != 0 ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemInt32Type) ||
            !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !UnchangedNativeSignature(method, definition, rawParameters) ||
            evidence.Code.Count < 7 ||
            evidence.Code[0].Op0Register != NativeRegister.ECX ||
            new X64CallingConventionResolver().ResolveForManaged(method) is not
                [ISIL.Register { Name: "rcx" }, ..])
            return false;

        var selector = method.Parameters[0];
        return selector.ParameterIndex == 0 &&
               ReferenceEquals(selector.DeclaringMethod, method) &&
               ReferenceEquals(selector.Definition, rawParameters[0]) &&
               selector.Definition?.RawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                   NumMods: 0, Byref: 0, Pinned: 0 } &&
               ReferenceEquals(selector.ParameterType, app.SystemTypes.SystemInt32Type) &&
               ReferenceEquals(selector.ParameterType, selector.DefaultParameterType) &&
               selector.Attributes == selector.DefaultAttributes &&
               selector.OverrideParameterType == null &&
               selector.OverrideAttributes == null &&
               !selector.UseOverrideDefaultValue && !selector.IsRef;
    }

    private static bool UnchangedNativeSignature(MethodAnalysisContext method,
        LibCpp2IL.Metadata.Il2CppMethodDefinition definition,
        LibCpp2IL.Metadata.Il2CppParameterDefinition[] rawParameters)
    {
        if (definition.parameterCount != method.Parameters.Count ||
            rawParameters.Length != method.Parameters.Count ||
            method.UnderlyingPointer == 0 ||
            !method.AppContext.MethodsByAddress.TryGetValue(
                method.UnderlyingPointer, out var bindings) ||
            bindings.Count != 1 || !ReferenceEquals(bindings[0], method))
            return false;

        for (var i = 0; i < rawParameters.Length; i++)
        {
            var parameter = method.Parameters[i];
            if (parameter.ParameterIndex != i ||
                !ReferenceEquals(parameter.DeclaringMethod, method) ||
                !ReferenceEquals(parameter.Definition, rawParameters[i]) ||
                rawParameters[i].RawType is not { NumMods: 0, Pinned: 0 } rawType ||
                rawType.Byref is not (0 or 1) ||
                (rawType.Byref == 1) !=
                (parameter.ParameterType is ByRefTypeAnalysisContext) ||
                !UnchangedParameterType(parameter) ||
                parameter.Attributes != parameter.DefaultAttributes ||
                parameter.OverrideParameterType != null ||
                parameter.OverrideAttributes != null ||
                parameter.UseOverrideDefaultValue ||
                parameter.Name != parameter.DefaultName)
                return false;
        }
        return true;
    }

    private static bool UnchangedParameterType(ParameterAnalysisContext parameter)
    {
        var current = parameter.ParameterType;
        var original = parameter.DefaultParameterType;
        return current is ByRefTypeAnalysisContext byRef &&
               original is ByRefTypeAnalysisContext originalByRef
            ? ReferenceEquals(byRef.ElementType, originalByRef.ElementType)
            : ReferenceEquals(current, original);
    }

    internal static bool DispatchValuesDeadAtTargets(
        X64ClosedSwitchTableProof.Evidence evidence)
    {
        var code = evidence.Code.ToDictionary(instruction => instruction.IP);
        var dispatch = code[evidence.Dispatch];
        var bodyStart = dispatch.NextIP;
        var scratch = new[]
        {
            evidence.Code[2].Op0Register.GetFullRegister(),
            evidence.Code[3].Op0Register.GetFullRegister(),
            evidence.Code[5].Op0Register.GetFullRegister(),
        };
        if (scratch.Distinct().Count() != scratch.Length)
            return false;

        var info = new InstructionInfoFactory();
        foreach (var target in evidence.CaseTargets.Append(evidence.DefaultTarget).Distinct())
        {
            var pending = new Stack<(ulong Address, int Defined)>();
            var visited = new HashSet<(ulong Address, int Defined)>();
            pending.Push((target, 0));
            while (pending.Count != 0)
            {
                var state = pending.Pop();
                if (!visited.Add(state))
                    continue;
                if (state.Address < bodyStart || state.Address >= evidence.Table ||
                    !code.TryGetValue(state.Address, out var instruction) ||
                    instruction.RflagsRead != RflagsBits.None)
                    return false;

                var defined = state.Defined;
                foreach (var used in info.GetInfo(instruction).GetUsedRegisters())
                {
                    var index = Array.IndexOf(scratch, used.Register.GetFullRegister());
                    if (index < 0)
                        continue;
                    var bit = 1 << index;
                    if (Reads(used.Access) && (defined & bit) == 0)
                        return false;
                    if (Writes(used.Access) && used.Register.GetSize() >= 4)
                        defined |= bit;
                }

                switch (instruction.FlowControl)
                {
                    case FlowControl.Next:
                        pending.Push((instruction.NextIP, defined));
                        break;
                    case FlowControl.ConditionalBranch:
                        pending.Push((instruction.NextIP, defined));
                        pending.Push((instruction.NearBranchTarget, defined));
                        break;
                    case FlowControl.UnconditionalBranch:
                        if (instruction.Op0Kind != OpKind.NearBranch64)
                            return false;
                        pending.Push((instruction.NearBranchTarget, defined));
                        break;
                    case FlowControl.Return:
                        break;
                    default:
                        return false;
                }
            }
        }
        return true;
    }

    private static bool Reads(OpAccess access) =>
        access is OpAccess.Read or OpAccess.CondRead or OpAccess.ReadWrite or
            OpAccess.ReadCondWrite;

    private static bool Writes(OpAccess access) =>
        access is OpAccess.Write or OpAccess.ReadWrite;
}
