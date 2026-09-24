using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using IsilInstruction = Cpp2IL.Core.ISIL.Instruction;
using IsilRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Closed exact-profile scalar array access: prove both runtime exception exits and the only
/// successful memory access before replacing the diamond with managed ldelem or stelem.
/// This does not generalize to unchecked native array access or other element types.
/// </summary>
internal static class X86ScalarArrayAccessProof
{
    internal static List<IsilInstruction>? TryLift(MethodAnalysisContext context, IReadOnlyList<Instruction> body)
    {
        var app = context.AppContext;
        var owner = context.DeclaringType;
        if (app.Binary is not PE { PointerSizeBytes: 8 } ||
            app.Binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
            app.UnityVersion.ToString() != "2021.3.35f1" ||
            context.Definition is not { GenericContainer: null } definition ||
            owner?.Definition is not { GenericContainer: null } ||
            owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            owner.Attributes != owner.DefaultAttributes || context.GenericParameters.Count != 0 ||
            !context.IsStatic || context.Attributes != context.DefaultAttributes ||
            context.ImplAttributes != context.DefaultImplAttributes ||
            (context.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (context.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                                       MethodImplAttributes.InternalCall)) != 0 ||
            context.Parameters.Count is not (2 or 3) ||
            definition.parameterCount != context.Parameters.Count ||
            definition.InternalParameterData?.Length != context.Parameters.Count)
            return null;

        var isWrite = context.Parameters.Count == 3;
        var array = context.Parameters[0];
        var index = context.Parameters[1];
        var rawArray = definition.InternalParameterData![0];
        var rawIndex = definition.InternalParameterData[1];
        if (array.Definition == null || index.Definition == null ||
            array.Definition != rawArray || index.Definition != rawIndex ||
            array.ParameterIndex != 0 || index.ParameterIndex != 1 ||
            !ReferenceEquals(array.DeclaringMethod, context) ||
            !ReferenceEquals(index.DeclaringMethod, context) ||
            array.IsRef || index.IsRef ||
            array.Attributes != array.DefaultAttributes || index.Attributes != index.DefaultAttributes ||
            array.OverrideParameterType != null || index.OverrideParameterType != null ||
            array.ParameterType is not SzArrayTypeAnalysisContext { ElementType: var element } ||
            (!ReferenceEquals(element, app.SystemTypes.SystemByteType) &&
             !ReferenceEquals(element, app.SystemTypes.SystemSByteType) &&
             !ReferenceEquals(element, app.SystemTypes.SystemInt32Type) &&
             !ReferenceEquals(element, app.SystemTypes.SystemUInt32Type) &&
             !ReferenceEquals(element, app.SystemTypes.SystemInt64Type) &&
             !ReferenceEquals(element, app.SystemTypes.SystemUInt64Type) &&
             !ReferenceEquals(element, app.SystemTypes.SystemSingleType)) ||
            !ReferenceEquals(index.ParameterType, app.SystemTypes.SystemInt32Type) ||
            array.Definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            index.Definition.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            context.OverrideReturnType != null ||
            definition.RawReturnType is not { NumMods: 0, Byref: 0, Pinned: 0 } rawReturn ||
            (isWrite
                ? !ReferenceEquals(context.ReturnType, app.SystemTypes.SystemVoidType) ||
                  rawReturn.Type != Il2CppTypeEnum.IL2CPP_TYPE_VOID ||
                  !ValidStoredParameter(context, definition, element)
                : !ReferenceEquals(context.ReturnType, element) ||
                  rawReturn.Type != RawElementType(app, element)) ||
            body.Count < 12 || body[0].IP != context.UnderlyingPointer)
            return null;

        var isByteElement = ReferenceEquals(element, app.SystemTypes.SystemByteType) ||
                            ReferenceEquals(element, app.SystemTypes.SystemSByteType);
        var isWideElement = ReferenceEquals(element, app.SystemTypes.SystemInt64Type) ||
                            ReferenceEquals(element, app.SystemTypes.SystemUInt64Type);
        var isSingleElement = ReferenceEquals(element, app.SystemTypes.SystemSingleType);
        var elementSize = isByteElement ? 1 : isWideElement ? 8 : 4;
        var nullCall = body[9];
        var boundsCall = body[11];
        if (!TryProveShape(body, isWrite, elementSize, isSingleElement))
            return null;
        var provedRegion = isSingleElement
            ? TryCompleteSingleRegion(app, context.UnderlyingPointer, body)
            : body;
        if (provedRegion == null ||
            X86RuntimeNullThrowProof.TryIdentify(app, nullCall.NearBranchTarget) == null ||
            !X86RuntimeBoundsThrowProof.TryIdentify(app, boundsCall.NearBranchTarget) ||
            X86CallerExceptionRegionProof.Check(context, provedRegion,
                new HashSet<ulong> { nullCall.IP, boundsCall.IP }) != null)
            return null;

        // ArrayRecovery recognizes this typed offset/scale as an element access.
        // The emitter preserves null-first and unsigned bounds failure via ldelem/stelem.
        var memory = new ISIL.MemoryOperand(new IsilRegister(null, "rcx"),
            new IsilRegister(null, "rdx"), 0x20, elementSize);
        if (isWrite)
            return
            [
                new(0, ISIL.OpCode.Move, memory, new IsilRegister(null, isSingleElement ? "xmm2" : "r8")),
                new(1, ISIL.OpCode.Return),
            ];
        var result = new IsilRegister(null, "array_read_result");
        return
        [
            new(0, ISIL.OpCode.Move, result, memory),
            new(1, ISIL.OpCode.Return, result),
        ];
    }

    private static bool ValidStoredParameter(MethodAnalysisContext context,
        LibCpp2IL.Metadata.Il2CppMethodDefinition definition, TypeAnalysisContext element)
    {
        var value = context.Parameters[2];
        return value.Definition != null && value.Definition == definition.InternalParameterData![2] &&
               value.ParameterIndex == 2 && ReferenceEquals(value.DeclaringMethod, context) &&
               !value.IsRef && value.Attributes == value.DefaultAttributes && value.OverrideParameterType == null &&
               ReferenceEquals(value.ParameterType, element) &&
               value.Definition.RawType is { NumMods: 0, Byref: 0, Pinned: 0 } raw &&
               raw.Type == RawElementType(context.AppContext, element);
    }

    private static Il2CppTypeEnum RawElementType(ApplicationAnalysisContext app, TypeAnalysisContext element)
    {
        if (ReferenceEquals(element, app.SystemTypes.SystemByteType))
            return Il2CppTypeEnum.IL2CPP_TYPE_U1;
        if (ReferenceEquals(element, app.SystemTypes.SystemSByteType))
            return Il2CppTypeEnum.IL2CPP_TYPE_I1;
        if (ReferenceEquals(element, app.SystemTypes.SystemUInt64Type))
            return Il2CppTypeEnum.IL2CPP_TYPE_U8;
        if (ReferenceEquals(element, app.SystemTypes.SystemInt64Type))
            return Il2CppTypeEnum.IL2CPP_TYPE_I8;
        if (ReferenceEquals(element, app.SystemTypes.SystemSingleType))
            return Il2CppTypeEnum.IL2CPP_TYPE_R4;
        return ReferenceEquals(element, app.SystemTypes.SystemUInt32Type)
            ? Il2CppTypeEnum.IL2CPP_TYPE_U4 : Il2CppTypeEnum.IL2CPP_TYPE_I4;
    }

    internal static IReadOnlyList<Instruction>? TryCompleteSingleRegion(ApplicationAnalysisContext app,
        ulong entry, IReadOnlyList<Instruction> body)
    {
        if (body.Count < 12)
            return null;
        // The exact native region has one INT3 after the proved nonreturning bounds call.
        // X86Utils may stop before that byte or decode onward through alignment into the next
        // function. Reconstruct the complete unwind region from file-backed player bytes.
        var trapAddress = body[11].NextIP;
        if (trapAddress == ulong.MaxValue || app.Binary is not PE pe ||
            !pe.TryMapVirtualAddressToRaw(trapAddress, out var raw))
            return null;
        var end = trapAddress + 1;
        var unwind = X64UnwindProof.ForApplication(app);
        var span = unwind?.ClassifySpan(entry, end);
        if (span is not { Kind: X64UnwindProof.SpanKind.HandlerFree } ||
            span.Value.Start != entry || span.Value.RootStart != entry || span.Value.End != end ||
            !unwind!.MatchesUnwind(entry, end, 4, 0, [4, 0x42]))
            return null;
        var bytes = pe.GetRawBinaryContent();
        if (raw < 0 || raw >= bytes.Length || bytes[(int)raw] != 0xCC)
            return null;
        var decoder = Decoder.Create(64, new ByteArrayCodeReader([0xCC]), trapAddress);
        var trap = decoder.Decode();
        if (trap.Code != Code.Int3 || trap.IP != trapAddress || trap.NextIP != end ||
            body.Count > 12 && body[12].IP < end &&
            (body[12].IP != trapAddress || body[12].Code != Code.Int3 || body[12].NextIP != end))
            return null;
        return body.Take(12).Append(trap).ToArray();
    }

    internal static bool TryProveShape(IReadOnlyList<Instruction> body, bool isWrite, int elementSize = 4,
        bool isSingleElement = false)
    {
        if (body.Count < 12 || elementSize is not (1 or 4 or 8) || isSingleElement && elementSize != 4)
            return false;
        for (var i = 0; i < 12; i++)
        {
            var instruction = body[i];
            if (instruction.IsInvalid || instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None ||
                (i > 0 && instruction.IP != body[i - 1].NextIP))
                return false;
        }
        var nullBranch = body[2];
        var boundsBranch = body[4];
        var length = body[3];
        var element = body[6];
        return Stack(body[0], Mnemonic.Sub, 0x28) &&
               Registers(body[1], Mnemonic.Test, Register.RCX, Register.RCX) &&
               nullBranch.Mnemonic == Mnemonic.Je && nullBranch.Op0Kind == OpKind.NearBranch64 &&
               nullBranch.NearBranchTarget == body[9].IP &&
               length.Mnemonic == Mnemonic.Cmp && length.OpCount == 2 &&
               length.Op0Kind == OpKind.Register && length.Op0Register == Register.EDX &&
               Memory(length, 1, Register.RCX, Register.None, 1, 0x18, 4) &&
               boundsBranch.Mnemonic == Mnemonic.Jae && boundsBranch.Op0Kind == OpKind.NearBranch64 &&
               boundsBranch.NearBranchTarget == body[11].IP &&
               body[5].Code == Code.Movsxd_r64_rm32 &&
               Registers(body[5], Mnemonic.Movsxd, Register.RAX, Register.EDX) &&
               SuccessfulElementAccess(element, isWrite, elementSize, isSingleElement) &&
               Stack(body[7], Mnemonic.Add, 0x28) &&
               body[8].Code == Code.Retnq && body[8].OpCount == 0 &&
               Call(body[9]) && body[10].Code == Code.Int3 &&
               Call(body[11]);
    }

    private static bool Registers(Instruction i, Mnemonic mnemonic, Register destination, Register source)
        => i.Mnemonic == mnemonic && i.OpCount == 2 && i.Op0Kind == OpKind.Register &&
           i.Op1Kind == OpKind.Register && i.Op0Register == destination && i.Op1Register == source;
    private static bool Stack(Instruction i, Mnemonic mnemonic, ulong size)
        => i.Mnemonic == mnemonic && i.OpCount == 2 && i.Op0Kind == OpKind.Register &&
           i.Op0Register == Register.RSP && i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 &&
           i.GetImmediate(1) == size;
    private static bool Memory(Instruction i, int operand, Register @base, Register index,
        int scale, ulong displacement, int size)
        => i.GetOpKind(operand) == OpKind.Memory && i.MemoryBase == @base &&
           i.MemoryIndex == index && i.MemoryIndexScale == scale &&
           i.MemoryDisplacement64 == displacement && i.MemorySize.GetSize() == size;
    private static bool ElementMemory(Instruction instruction, int operand, int elementSize)
        => elementSize == 1
            // With scale one, the address is commutative, but require the exact
            // encoding observed in the one-byte fixture's successful arm.
            ? Memory(instruction, operand, Register.RAX, Register.RCX, 1, 0x20, 1)
            : Memory(instruction, operand, Register.RCX, Register.RAX, elementSize, 0x20, elementSize);
    private static bool SuccessfulElementAccess(Instruction instruction, bool isWrite, int elementSize,
        bool isSingleElement)
    {
        if (instruction.OpCount != 2)
            return false;
        if (isSingleElement)
            return isWrite
                ? instruction.Code == Code.Movss_xmmm32_xmm &&
                  ElementMemory(instruction, 0, 4) &&
                  instruction.Op1Kind == OpKind.Register && instruction.Op1Register == Register.XMM2
                : instruction.Code == Code.Movss_xmm_xmmm32 &&
                  instruction.Op0Kind == OpKind.Register && instruction.Op0Register == Register.XMM0 &&
                  ElementMemory(instruction, 1, 4);
        if (isWrite)
        {
            var valueRegister = elementSize == 8 ? Register.R8 :
                elementSize == 1 ? Register.R8L : Register.R8D;
            return instruction.Mnemonic == Mnemonic.Mov &&
                   ElementMemory(instruction, 0, elementSize) &&
                   instruction.Op1Kind == OpKind.Register && instruction.Op1Register == valueRegister;
        }

        // The exact player folds byte[] and sbyte[] reads into one MOVZX body.
        // Only AL is part of the 8-bit native return value; callers extend it
        // according to their managed signature. Typed ldelem preserves that.
        var loadMatches = elementSize == 1
            ? instruction.Code == Code.Movzx_r32_rm8
            : instruction.Mnemonic == Mnemonic.Mov;
        return loadMatches && instruction.Op0Kind == OpKind.Register &&
               instruction.Op0Register == (elementSize == 8 ? Register.RAX : Register.EAX) &&
               ElementMemory(instruction, 1, elementSize);
    }
    private static bool Call(Instruction i)
        => i.Code == Code.Call_rel32_64 && i.Op0Kind == OpKind.NearBranch64 &&
           i.NearBranchTarget != 0;
}
