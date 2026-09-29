using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Binds a complete open-generic getter leaf to a field before the first
/// instantiation-dependent field. Later VAR/value storage has no inferred size.
/// </summary>
internal static class OpenGenericEarlyFieldProof
{
    internal const string EvidenceKey = "OpenGenericEarlyFieldProof";
    internal sealed record Shape(NativeInstruction Load, NativeInstruction Return, int Offset, int Width);
    internal sealed record Evidence(Shape Native, TypeAnalysisContext ReceiverType,
        FieldAnalysisContext Field, ManagedInstruction Read, LocalVariable Receiver,
        object[] Metadata, byte[] NativeBytes);

    internal static List<ManagedInstruction>? TryLift(MethodAnalysisContext method)
    {
        if (Find(method, null) is not { } candidate)
            return null;
        var receiver = new ManagedRegister(null, "rcx");
        var result = new ManagedRegister(null, "rax");
        return
        [
            new ManagedInstruction(0, OpCode.Move, result,
                new ISIL.MemoryOperand(receiver, null, candidate.Native.Offset))
                { NativeAddress = candidate.Native.Load.IP },
            new ManagedInstruction(1, OpCode.Return, result)
                { NativeAddress = candidate.Native.Return.IP }
        ];
    }

    internal static FieldAnalysisContext? TryResolve(MethodAnalysisContext method,
        ManagedInstruction instruction, ISIL.MemoryOperand memory, TypeAnalysisContext owner)
    {
        if (instruction is not { OpCode: OpCode.Move, NativeAddress: not null } ||
            instruction.Operands.Count != 2 || instruction.Operands[1] is not ISIL.MemoryOperand actual ||
            actual.Base is not LocalVariable { IsThis: true, IsMethodInfo: false } receiver ||
            !ReferenceEquals(receiver.Type, owner) || receiver.Register is not { Name: "rcx", Version: -1 } ||
            !ReferenceEquals(actual.Base, memory.Base) || actual.Addend != memory.Addend ||
            actual.Index != null || actual.Scale != 0 ||
            Find(method, owner) is not { } candidate ||
            instruction.NativeAddress != candidate.Native.Load.IP ||
            actual.Addend != candidate.Native.Offset)
            return null;
        var prior = method.GetExtraData<Evidence>(EvidenceKey);
        if (prior != null && (!ReferenceEquals(prior.Read, instruction) ||
                              !ReferenceEquals(prior.Field, candidate.Field)))
            return null;
        var nativeBytes = CurrentBodyBytes(method, candidate.Native);
        if (nativeBytes == null)
            return null;
        method.PutExtraData(EvidenceKey, new Evidence(candidate.Native, owner, candidate.Field,
            instruction, receiver, candidate.Metadata, nativeBytes));
        NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        return candidate.Field;
    }

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey) ||
        method.GetExtraData<Evidence>(EvidenceKey) != null;

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        if (method.GetExtraData<Evidence>(EvidenceKey) is not { } saved ||
            Find(method, saved.ReceiverType) is not { } current ||
            !ReferenceEquals(saved.Field, current.Field) || saved.Native != current.Native ||
            !saved.Metadata.SequenceEqual(current.Metadata) ||
            CurrentBodyBytes(method, current.Native) is not { } bytes ||
            !bytes.SequenceEqual(saved.NativeBytes))
            return false;
        var expected = new X64CallingConventionResolver().ResolveForParameters(method);
        if (
            method.ControlFlowGraph is not { } graph ||
            graph.EntryBlock.Instructions.Count != 0 || graph.ExitBlock.Instructions.Count != 0 ||
            graph.EntryBlock.Predecessors.Count != 0 || graph.ExitBlock.Successors.Count != 0 ||
            graph.Blocks.Where(block => block != graph.EntryBlock && block != graph.ExitBlock).ToArray() is not [var body] ||
            graph.EntryBlock.Successors is not [var entry] || !ReferenceEquals(entry, body) ||
            body.Predecessors is not [var predecessor] || !ReferenceEquals(predecessor, graph.EntryBlock) ||
            body.Successors is not [var exit] || !ReferenceEquals(exit, graph.ExitBlock) ||
            graph.ExitBlock.Predecessors is not [var final] || !ReferenceEquals(final, body) ||
            body.Instructions.ToArray() is not [var read, var ret] ||
            !ReferenceEquals(read, saved.Read) ||
            read is not { OpCode: OpCode.Move, IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct, Operands: [LocalVariable value,
                    FieldReference field] } ||
            read.NativeAddress != current.Native.Load.IP ||
            !(ReferenceEquals(saved.ReceiverType, method.DeclaringType)
                ? ReferenceEquals(field.Field, current.Field)
                : field.Field is ConcreteGenericFieldAnalysisContext projected &&
                  ReferenceEquals(projected.BaseFieldContext, current.Field) &&
                  ReferenceEquals(projected.DeclaringType, saved.ReceiverType) &&
                  projected.OverrideFieldType == null && projected.OverrideOffset == null &&
                  ReferenceEquals(projected.FieldType, current.Field.FieldType)) ||
            field.Offset != current.Native.Offset ||
            !ReferenceEquals(field.Local, saved.Receiver) ||
            !ReferenceEquals(value.Type, method.ReturnType) ||
            ret is not { OpCode: OpCode.Return, IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct, Operands: [LocalVariable returned] } ||
            ret.NativeAddress != current.Native.Return.IP || !ReferenceEquals(returned, value) ||
            expected.Length < 1 || expected[0] is not ManagedRegister
                { Name: "rcx", Version: -1 } originalReceiver ||
            !expected.SequenceEqual(method.ParameterOperands) ||
            method.ParameterLocals.ToArray() is not [{ IsThis: true, IsMethodInfo: false } incoming] ||
            !ReferenceEquals(incoming, saved.Receiver) ||
            !ReferenceEquals(incoming.Type, saved.ReceiverType) ||
            incoming.Register != originalReceiver ||
            method.Locals.Count(local => ReferenceEquals(local, incoming)) > 1 ||
            method.Locals.Any(local => local.Register.Number == incoming.Register.Number &&
                                      !ReferenceEquals(local, incoming)) ||
            graph.Instructions.Any(operation => ReferenceEquals(operation.Destination, incoming)) ||
            method.Locals.Count(local => ReferenceEquals(local, value)) != 1 ||
            method.ParameterLocals.Contains(value) || value.IsThis || value.IsMethodInfo ||
            value.Register.Copy() != new ManagedRegister(null, "rax") ||
            value.Register.Version < 0 ||
            graph.Instructions.Count(operation => ReferenceEquals(operation.Destination, value)) != 1 ||
            OperandEffects.LocalsWithMutableStorage(body.Instructions).Count != 0)
            return false;
        return true;
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> code)
    {
        if (code is not [var load, var ret] || load.IsInvalid ||
            load.CodeSize != CodeSize.Code64 || load.HasLockPrefix || load.HasRepPrefix ||
            load.HasRepnePrefix || load.SegmentPrefix != Iced.Intel.Register.None ||
            ret.Code != Code.Retnq || ret.OpCount != 0 || load.NextIP != ret.IP ||
            load.OpCount != 2 || load.Op0Kind != OpKind.Register ||
            load.Op1Kind != OpKind.Memory || load.MemoryBase != Iced.Intel.Register.RCX ||
            load.MemoryIndex != Iced.Intel.Register.None || load.MemoryIndexScale != 1 ||
            load.MemoryDisplacement64 is < 16 or > 4095)
            return null;
        var width = load.Code switch
        {
            Code.Mov_r32_rm32 when load.Op0Register == Iced.Intel.Register.EAX => 32,
            Code.Mov_r64_rm64 when load.Op0Register == Iced.Intel.Register.RAX => 64,
            Code.Movzx_r32_rm8 when load.Op0Register == Iced.Intel.Register.EAX => 8,
            _ => 0,
        };
        return width != 0 && load.MemorySize.GetSize() == width / 8
            ? new Shape(load, ret, (int)load.MemoryDisplacement64, width) : null;
    }

    private sealed record Candidate(Shape Native, FieldAnalysisContext Field, object[] Metadata);

    private static Candidate? Find(MethodAnalysisContext method, TypeAnalysisContext? requiredOwner)
    {
        var app = method.AppContext;
        if (app.Binary is not PE { PointerSizeBytes: 8 } ||
            !X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            method.DeclaringType is not { Definition: { } definition } declared ||
            method.Definition is not { GenericContainer: null, parameterCount: 0 } rawMethod ||
            !ReferenceEquals(rawMethod.DeclaringType, definition) ||
            method.IsStatic || method.IsVirtual || method.IsAbstract ||
            method.Parameters.Count != 0 || method.GenericParameters.Count != 0 ||
            (rawMethod.InternalParameterData?.Length ?? 0) != 0 ||
            method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
            rawMethod.RawReturnType is not { NumMods: 0, Byref: 0, Pinned: 0 } rawReturn ||
            rawReturn.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or
                Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or
                Il2CppTypeEnum.IL2CPP_TYPE_STRING or Il2CppTypeEnum.IL2CPP_TYPE_CLASS) ||
            !app.Binary.TryGetTypeVirtualAddress(rawReturn, out var returnAddress) ||
            !ReferenceEquals(ClosedGenericValueLayoutProof.ReadUnchangedType(app, returnAddress), rawReturn) ||
            method.OverrideReturnType != null || !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
            !HasOriginalNativeSignature(method, definition, rawMethod) ||
            X64NativeInstructionReader.ReadFramelessLeaf(method, 2, 32) is not { } body ||
            TryProveShape(body) is not { } shape ||
            !OriginalOpenOwner(method, requiredOwner, declared))
            return null;
        var owner = requiredOwner ?? declared;
        if (PrefixField(method, declared, shape.Offset, shape.Width) is not { } field ||
            rawReturn.Type != field.BackingData?.Field.RawFieldType?.Type ||
            !ReferenceEquals(method.ReturnType, field.FieldType))
            return null;
        return Snapshot(method, owner) is { } metadata
            ? new Candidate(shape, field, metadata) : null;
    }

    private static bool HasOriginalNativeSignature(MethodAnalysisContext method,
        Il2CppTypeDefinition owner, Il2CppMethodDefinition definition)
    {
        // The ordinary signature helper excludes generic declaring types. This
        // leaf binds its own original generic owner and complete native body.
        return ReferenceEquals(definition.DeclaringType, owner) &&
               definition.GenericContainer == null && definition.parameterCount == 0 &&
               (definition.InternalParameterData?.Length ?? 0) == 0 &&
               method.Parameters.Count == 0 && method.UnderlyingPointer != 0 &&
               method.AppContext.MethodsByAddress.TryGetValue(method.UnderlyingPointer,
                   out var bindings) &&
               bindings.Any(bound => ReferenceEquals(bound, method));
    }

    internal static bool OriginalOpenOwner(MethodAnalysisContext method, TypeAnalysisContext? owner,
        TypeAnalysisContext declared)
    {
        if (declared.IsValueType || declared.IsInterface || declared.GenericParameters.Count is < 1 or > 8 ||
            declared.Name != declared.DefaultName || declared.Namespace != declared.DefaultNamespace ||
            declared.Attributes != declared.DefaultAttributes ||
            declared.Definition is not { IsValueType: false, IsEnumType: false,
                PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0 },
                GenericContainer: { } container } ||
            container.isGenericMethod || !ReferenceEquals(container.TypeOwner, declared.Definition) ||
            container.genericParameterCount != declared.GenericParameters.Count ||
            (declared.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout ||
            declared.OverrideBaseType != null ||
            declared.Definition.RawBaseType is not { NumMods: 0, Byref: 0, Pinned: 0 } rawBase ||
            rawBase.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_CLASS or
                Il2CppTypeEnum.IL2CPP_TYPE_OBJECT) ||
            !method.AppContext.Binary.TryGetTypeVirtualAddress(rawBase, out var baseAddress) ||
            !ReferenceEquals(ClosedGenericValueLayoutProof.ReadUnchangedType(method.AppContext, baseAddress), rawBase) ||
            !ReferenceEquals(method.AppContext.ResolveIl2CppType(rawBase), method.AppContext.SystemTypes.SystemObjectType) ||
            !ReferenceEquals(declared.DefaultBaseType, method.AppContext.SystemTypes.SystemObjectType) ||
            !ReferenceEquals(declared.BaseType, method.AppContext.SystemTypes.SystemObjectType) ||
            declared.BaseType.Fields.Any(field => !field.IsStatic))
            return false;
        if (owner == null || ReferenceEquals(owner, declared))
            return true;
        return owner is GenericInstanceTypeAnalysisContext open &&
            open.OriginalRawType == null && !open.IsValueType &&
            ReferenceEquals(open.GenericType, declared) &&
            open.GenericArguments.Count == declared.GenericParameters.Count &&
            open.GenericArguments.Select((argument, index) =>
                ReferenceEquals(argument, declared.GenericParameters[index]) &&
                argument is GenericParameterTypeAnalysisContext parameter &&
                parameter.Type == Il2CppTypeEnum.IL2CPP_TYPE_VAR &&
                parameter.Index == index && ReferenceEquals(parameter.Owner, declared) &&
                parameter.Attributes == parameter.DefaultAttributes).All(valid => valid);
    }

    internal static FieldAnalysisContext? PrefixField(MethodAnalysisContext method,
        TypeAnalysisContext owner, int targetOffset, int nativeWidth)
    {
        if (owner.Definition is not { } original ||
            !owner.Fields.Select(field => field.BackingData?.Field).SequenceEqual(original.Fields!) ||
            owner.Fields.Count != original.FieldCount)
            return null;
        try
        {
            var offset = 16L;
            foreach (var field in owner.Fields)
            {
                if (field.BackingData is not { Field: { } source } ||
                    !ReferenceEquals(source.DeclaringType, original) ||
                    field.Name != field.DefaultName || field.Attributes != field.DefaultAttributes ||
                    field.OverrideFieldType != null || field.OverrideOffset != null ||
                    field.Attributes.HasFlag(FieldAttributes.HasFieldMarshal) ||
                    source.RawFieldType is not { NumMods: 0, Byref: 0, Pinned: 0 } raw ||
                    !method.AppContext.Binary.TryGetTypeVirtualAddress(raw, out var address) ||
                    !ReferenceEquals(ClosedGenericValueLayoutProof.ReadUnchangedType(method.AppContext, address), raw))
                    return null;
                if (field.IsStatic)
                    continue;
                if ((field.Attributes & FieldAttributes.Literal) != 0 || Storage(raw) is not { } size)
                    return null;
                offset = checked((offset + size - 1) & -size);
                if (offset == targetOffset)
                    return size * 8 == nativeWidth && TargetType(raw, field, method.AppContext)
                        ? field : null;
                if (offset > targetOffset)
                    return null;
                offset = checked(offset + size);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            NullReferenceException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
        return null;
    }

    private static int? Storage(Il2CppType raw)
    {
        return raw.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_I1 or
                Il2CppTypeEnum.IL2CPP_TYPE_U1 => 1,
            Il2CppTypeEnum.IL2CPP_TYPE_CHAR or Il2CppTypeEnum.IL2CPP_TYPE_I2 or
                Il2CppTypeEnum.IL2CPP_TYPE_U2 => 2,
            Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or
                Il2CppTypeEnum.IL2CPP_TYPE_R4 => 4,
            Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 or
                Il2CppTypeEnum.IL2CPP_TYPE_R8 or Il2CppTypeEnum.IL2CPP_TYPE_I or
                Il2CppTypeEnum.IL2CPP_TYPE_U => 8,
            Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or Il2CppTypeEnum.IL2CPP_TYPE_STRING or
                Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_ARRAY or
                Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY or Il2CppTypeEnum.IL2CPP_TYPE_PTR => 8,
            Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST when raw.GetGenericClass().TypeDefinition is
                { IsValueType: false } => 8,
            _ => null,
        };
    }

    private static bool TargetType(Il2CppType raw, FieldAnalysisContext field,
        ApplicationAnalysisContext app)
    {
        var type = field.FieldType;
        var system = app.SystemTypes;
        return raw.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN => ReferenceEquals(type, system.SystemBooleanType),
            Il2CppTypeEnum.IL2CPP_TYPE_I4 => ReferenceEquals(type, system.SystemInt32Type),
            Il2CppTypeEnum.IL2CPP_TYPE_U4 => ReferenceEquals(type, system.SystemUInt32Type),
            Il2CppTypeEnum.IL2CPP_TYPE_OBJECT => ReferenceEquals(type, system.SystemObjectType),
            Il2CppTypeEnum.IL2CPP_TYPE_STRING => ReferenceEquals(type, system.SystemStringType),
            Il2CppTypeEnum.IL2CPP_TYPE_CLASS => !type.IsValueType &&
                ReferenceEquals(type, app.ResolveIl2CppType(raw)),
            _ => false,
        };
    }

    private static object[]? Snapshot(MethodAnalysisContext method, TypeAnalysisContext receiver)
    {
        var owner = method.DeclaringType!;
        var definition = owner.Definition!;
        var raw = method.Definition!;
        try
        {
            var values = new List<object>
            {
                owner, receiver, owner.Name, owner.Namespace, owner.Attributes,
                owner.BaseType!, definition.NameIndex, definition.NamespaceIndex,
                definition.Flags, definition.Bitfield, definition.Token,
                definition.ByvalTypeIndex, definition.ParentIndex,
                definition.GenericContainerIndex, definition.FirstFieldIdx,
                definition.FieldCount, method.Name, method.Attributes,
                method.ImplAttributes, raw.nameIndex, raw.token, raw.returnTypeIdx,
                raw.parameterCount, raw.parameterStart,
                raw.RawReturnType!.Datapoint, raw.RawReturnType.Bits,
            };
            foreach (var field in owner.Fields)
            {
                var source = field.BackingData!.Field;
                values.Add(field); values.Add(field.Name); values.Add(field.Attributes);
                values.Add(field.Offset);
                values.Add(source.nameIndex); values.Add(source.typeIndex); values.Add(source.token);
                values.Add(source.RawFieldType!.Datapoint); values.Add(source.RawFieldType.Bits);
            }
            return values.ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            NullReferenceException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static byte[]? CurrentBodyBytes(MethodAnalysisContext method, Shape shape)
    {
        if (method.AppContext.Binary is not PE pe || shape.Load.IP != method.UnderlyingPointer)
            return null;
        var length = checked((int)(shape.Return.NextIP - shape.Load.IP));
        var offset = pe.MapVirtualAddressToRaw(shape.Load.IP, false);
        var image = pe.GetRawBinaryContent();
        return offset >= 0 && offset <= image.Length - length
            ? image.Slice(checked((int)offset), length).ToArray() : null;
    }
}
