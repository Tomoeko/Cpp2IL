using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Replaces an inlined byte store to an inherited private Boolean backing
/// field with its public nonvirtual setter. The setter's complete native leaf
/// must perform exactly that store and return. This proves equivalent behavior,
/// not the identity of the original managed callsite.
/// </summary>
internal static class X64InlinedBooleanSetterProof
{
    internal sealed record Evidence(Instruction Call, FieldReference NativeAccess,
        LocalVariable Receiver, bool Value, MethodAnalysisContext Setter,
        RuntimeNullGuardCoalescer.FieldAccessEvidence EarlierRead)
    {
        internal bool IsValidFor(MethodAnalysisContext caller)
        {
            if (caller.ControlFlowGraph?.Instructions.Contains(Call) != true ||
                Call is not { OpCode: OpCode.CallVoid, IntegerBitWidth: 0,
                    CallSemantics: CallSemantics.NullCheckedInstance } ||
                Call.Operands.Count is not (3 or 4) ||
                Call.Operands[0] is not MethodAnalysisContext target ||
                Call.Operands[1] is not LocalVariable receiver ||
                Call.Operands[2] is not Immediate value ||
                Call.Operands.Count == 4 &&
                Call.Operands[3] is not Immediate { Value: 0 } ||
                !ReferenceEquals(target, Setter) ||
                !ReferenceEquals(receiver, Receiver) ||
                !ReferenceEquals(receiver, NativeAccess.Local) ||
                !caller.NullCheckedFieldAccesses.Contains(EarlierRead) ||
                !EarlierRead.IsValidFor(caller) ||
                !ReferenceEquals(EarlierRead.Receiver, Receiver) ||
                EarlierRead.StoredValue != null ||
                !HasEarlierImplicitCheck(caller, EarlierRead, Call) ||
                value.Value != (Value ? 1 : 0) ||
                !NullCheckedCall.TryGet(Call, out var checkedTarget,
                    out var checkedReceiver) ||
                !ReferenceEquals(checkedTarget, Setter) ||
                !ReferenceEquals(checkedReceiver, Receiver))
                return false;
            return ReferenceEquals(Find(caller, NativeAccess, Value,
                Call.NativeAddress), Setter);
        }
    }

    internal static Evidence? TryRewrite(MethodAnalysisContext caller,
        Instruction store, RuntimeNullGuardCoalescer.FieldAccessEvidence earlierRead)
    {
        if (store is not { OpCode: OpCode.Move, IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct,
                Operands: [FieldReference access, Immediate literal] } ||
            literal.Value is not (0 or 1) ||
            !ReferenceEquals(earlierRead.Receiver, access.Local) ||
            earlierRead.StoredValue != null ||
            !caller.NullCheckedFieldAccesses.Contains(earlierRead) ||
            !earlierRead.IsValidFor(caller) ||
            !HasEarlierImplicitCheck(caller, earlierRead, store) ||
            Find(caller, access, literal.Value == 1, store.NativeAddress) is not
                { } setter)
            return null;

        var evidence = new Evidence(store, access, access.Local,
            literal.Value == 1, setter, earlierRead);
        store.OpCode = OpCode.CallVoid;
        store.SetOperands(setter, access.Local, literal, new Immediate(0));
        store.CallSemantics = CallSemantics.NullCheckedInstance;
        return evidence;
    }

    internal static bool HasEarlierImplicitCheck(MethodAnalysisContext caller,
        RuntimeNullGuardCoalescer.FieldAccessEvidence read,
        Instruction later)
    {
        var graph = caller.ControlFlowGraph;
        if (graph == null ||
            graph.FindBlockByInstruction(read.Operation) is not { } readBlock ||
            graph.FindBlockByInstruction(later) is not { } laterBlock)
            return false;
        if (ReferenceEquals(readBlock, laterBlock))
            return readBlock.Instructions.IndexOf(read.Operation) >= 0 &&
                   readBlock.Instructions.IndexOf(read.Operation) <
                   readBlock.Instructions.IndexOf(later);
        return new DominatorInfo(graph).Dominates(readBlock, laterBlock);
    }

    private static MethodAnalysisContext? Find(MethodAnalysisContext caller,
        FieldReference access, bool value, ulong? nativeStoreAddress)
    {
        var app = caller.AppContext;
        var field = access.Field;
        var owner = field.DeclaringType;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(caller) ||
            app.Binary is not PE pe ||
            X64UnwindProof.ForApplication(app) is not { } unwind ||
            nativeStoreAddress is not { } storeAddress ||
            !ReferenceEquals(field.FieldType, app.SystemTypes.SystemBooleanType) ||
            field.IsStatic || field.Offset < 16 ||
            access.Offset != field.Offset ||
            field.Offset != field.DefaultOffset ||
            field.Attributes != field.DefaultAttributes ||
            (field.Attributes & FieldAttributes.FieldAccessMask) !=
                FieldAttributes.Private ||
            (field.Attributes & (FieldAttributes.InitOnly |
                                 FieldAttributes.Literal |
                                 FieldAttributes.HasDefault |
                                 FieldAttributes.HasFieldMarshal |
                                 FieldAttributes.HasFieldRVA)) != 0 ||
            field.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            owner.Definition is not { GenericContainer: null,
                HasCctor: false, PackingSizeIsDefault: true,
                ClassSizeIsDefault: true } ||
            !NullCheckedCall.IsReferenceClass(owner) ||
            owner.Name != owner.DefaultName ||
            owner.Namespace != owner.DefaultNamespace ||
            !HasUnchangedInheritedReceiver(access.Local.Type, owner) ||
            owner.Fields.Where(candidate => !candidate.IsStatic &&
                candidate.Offset == field.Offset).ToArray() is not
                [{ } onlyField] || !ReferenceEquals(onlyField, field) ||
            !NarrowFieldEqualityProof.HasUnchangedByteFieldLayout(
                new FieldReference(field,
                    new LocalVariable("proved-owner",
                        new ISIL.Register(null, "rcx"), owner),
                    access.Offset)) ||
            !HasNativeCallerStore(caller, pe, unwind, storeAddress,
                access, value))
            return null;

        var propertyName = field.Name;
        if (!propertyName.StartsWith("<", StringComparison.Ordinal) ||
            !propertyName.EndsWith(">k__BackingField",
                StringComparison.Ordinal))
            return null;
        propertyName = propertyName[1..^16];
        var properties = owner.Properties.Where(property =>
            property.Name == propertyName &&
            property.Name == property.DefaultName &&
            !property.IsStatic &&
            property.Attributes == property.DefaultAttributes &&
            property.OverridePropertyType == null &&
            ReferenceEquals(property.PropertyType,
                app.SystemTypes.SystemBooleanType) &&
            property.Definition?.RawPropertyType is
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                    NumMods: 0, Byref: 0, Pinned: 0 }).ToArray();
        if (properties is not [{ Setter: { } setter } property] ||
            !ReferenceEquals(property.DeclaringType, owner) ||
            !ValidSetterMetadata(setter, property, owner,
                access.Local.Type!) ||
            !HasExactSetterBody(setter, pe, unwind, access.Offset))
            return null;
        return setter;
    }

    private static bool HasUnchangedInheritedReceiver(
        TypeAnalysisContext? receiver, TypeAnalysisContext owner)
    {
        var visited = new HashSet<TypeAnalysisContext>();
        var inherited = false;
        for (var type = receiver; type != null; type = type.BaseType)
        {
            if (!visited.Add(type) || !NullCheckedCall.IsReferenceClass(type) ||
                type.Definition is not { GenericContainer: null,
                    HasCctor: false, PackingSizeIsDefault: true,
                    ClassSizeIsDefault: true })
                return false;
            if (ReferenceEquals(type, owner))
                return inherited;
            inherited = true;
        }
        return false;
    }

    private static bool ValidSetterMetadata(MethodAnalysisContext setter,
        PropertyAnalysisContext property, TypeAnalysisContext owner,
        TypeAnalysisContext receiverType)
    {
        var app = setter.AppContext;
        var receiverOwners = new HashSet<TypeAnalysisContext>();
        for (var type = receiverType; type != null; type = type.BaseType)
            if (!receiverOwners.Add(type) || !NullCheckedCall.IsReferenceClass(type))
                return false;
        if (!receiverOwners.Contains(owner))
            return false;
        if (!ReferenceEquals(setter.DeclaringType, owner) ||
            !ReferenceEquals(setter.AppContext, owner.AppContext) ||
            !ReferenceEquals(property.Setter, setter) ||
            !ReferenceEquals(property.Definition?.Setter,
                setter.Definition) ||
            setter.Name != "set_" + property.Name ||
            setter.Name != setter.DefaultName ||
            setter.IsStatic || setter.IsVirtual || !setter.IsVoid ||
            setter.GenericParameters.Count != 0 ||
            setter.Attributes != setter.DefaultAttributes ||
            setter.ImplAttributes != setter.DefaultImplAttributes ||
            (setter.Attributes & (MethodAttributes.MemberAccessMask |
                                  MethodAttributes.SpecialName |
                                  MethodAttributes.Abstract |
                                  MethodAttributes.PinvokeImpl)) !=
                (MethodAttributes.Public | MethodAttributes.SpecialName) ||
            (setter.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                      MethodImplAttributes.ManagedMask |
                                      MethodImplAttributes.InternalCall)) != 0 ||
            setter.OverrideReturnType != null ||
            !ReferenceEquals(setter.ReturnType,
                app.SystemTypes.SystemVoidType) ||
            setter.Definition is not { GenericContainer: null,
                parameterCount: 1,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) ||
            definition.InternalParameterData is not [var rawParameter] ||
            rawParameter.RawType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                    NumMods: 0, Byref: 0, Pinned: 0 } ||
            setter.Parameters is not [var parameter] ||
            !ReferenceEquals(parameter.Definition, rawParameter) ||
            !ReferenceEquals(parameter.DeclaringMethod, setter) ||
            parameter.ParameterIndex != 0 || parameter.IsRef ||
            parameter.Attributes != parameter.DefaultAttributes ||
            parameter.OverrideParameterType != null ||
            !ReferenceEquals(parameter.ParameterType,
                app.SystemTypes.SystemBooleanType) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(setter) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(setter,
                requireUniqueBinding: false) ||
            !app.MethodsByAddress.TryGetValue(setter.UnderlyingPointer,
                out var aliases) ||
            aliases.Count(candidate => ReferenceEquals(candidate, setter)) != 1 ||
            aliases.Any(candidate => candidate.IsStatic ||
                candidate.DeclaringType == null) ||
            aliases.Where(candidate => receiverOwners.Contains(
                candidate.DeclaringType!)).ToArray() is not
                [var applicable] || !ReferenceEquals(applicable, setter))
            return false;
        return true;
    }

    private static bool HasExactSetterBody(MethodAnalysisContext setter,
        PE pe, X64UnwindProof.Index unwind, int fieldOffset)
    {
        try
        {
            setter.EnsureRawBytes();
            var start = setter.UnderlyingPointer;
            if (setter.RawBytes.Length < 4 ||
                start > ulong.MaxValue - (ulong)setter.RawBytes.Length)
                return false;
            var body = X86Utils.Iterate(setter.RawBytes.AsSpan(), start, false);
            if (body.Count < 2 || body[0].IP != start ||
                body[0].Code != Code.Mov_rm8_r8 ||
                body[0].Op0Kind != OpKind.Memory ||
                body[0].MemoryBase != NativeRegister.RCX ||
                body[0].MemoryIndex != NativeRegister.None ||
                body[0].MemoryDisplacement64 != (ulong)fieldOffset ||
                body[0].MemorySize.GetSize() != 1 ||
                body[0].Op1Kind != OpKind.Register ||
                body[0].Op1Register != NativeRegister.DL ||
                body[1].Code != Code.Retnq || body[1].OpCount != 0 ||
                body[1].IP != body[0].NextIP ||
                body.Take(2).Any(instruction => instruction.IsInvalid ||
                    instruction.CodeSize != CodeSize.Code64 ||
                    instruction.HasLockPrefix || instruction.HasRepPrefix ||
                    instruction.HasRepnePrefix ||
                    instruction.SegmentPrefix != NativeRegister.None))
                return false;

            var leafEnd = body[1].NextIP;
            var index = 2;
            var paddedEnd = leafEnd;
            while (index < body.Count && body[index].Code == Code.Int3)
            {
                if (body[index].IP != paddedEnd ||
                    body[index].Length != 1 ||
                    paddedEnd - leafEnd >= 16)
                    return false;
                paddedEnd = body[index].NextIP;
                index++;
            }
            if (paddedEnd - start > (ulong)setter.RawBytes.Length ||
                !X64NativePaddingProof.HasInt3Padding(pe, leafEnd,
                    paddedEnd) ||
                !X64AncestorConstructorThunkProof.FileBackedExecutable(pe,
                    unwind, setter.RawBytes.AsSpan().Slice(0,
                        checked((int)(paddedEnd - start))), start) ||
                unwind.ClassifySpan(start, paddedEnd).Kind !=
                    X64UnwindProof.SpanKind.NoEntry ||
                Enumerable.Range(1, checked((int)(paddedEnd - start - 1)))
                    .Any(offset => setter.AppContext.MethodsByAddress
                        .ContainsKey(start + (ulong)offset)) ||
                X86CallerExceptionRegionProof.Check(setter,
                    body.Take(2).ToArray(), new HashSet<ulong>()) != null)
                return false;
            if (index == body.Count)
                return paddedEnd == start + (ulong)setter.RawBytes.Length;

            // A method-size estimate may include the next unrelated function.
            // Trap padding and that function's own unwind entry delimit the leaf.
            var next = body[index];
            return paddedEnd > leafEnd && next.IP == paddedEnd &&
                   !next.IsInvalid && next.CodeSize == CodeSize.Code64 &&
                   unwind.ClassifySpan(paddedEnd, next.NextIP) is
                       { Kind: X64UnwindProof.SpanKind.HandlerFree,
                           Start: var nextStart, RootStart: var nextRoot } &&
                   nextStart == paddedEnd && nextRoot == paddedEnd;
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool HasNativeCallerStore(MethodAnalysisContext caller,
        PE pe, X64UnwindProof.Index unwind, ulong address,
        FieldReference access, bool value)
    {
        if (address < caller.UnderlyingPointer ||
            address - caller.UnderlyingPointer > int.MaxValue - 15 ||
            !Enum.TryParse<NativeRegister>(access.Local.Register.Name,
                true, out var receiverRegister))
            return false;
        try
        {
            caller.EnsureRawBytes();
            var native = X86Utils.Iterate(caller).Where(instruction =>
                instruction.IP == address).ToArray();
            if (native is not [{ Code: Code.Mov_rm8_imm8,
                    Op0Kind: OpKind.Memory, Op1Kind: OpKind.Immediate8 } store] ||
                store.IsInvalid || store.CodeSize != CodeSize.Code64 ||
                store.HasLockPrefix || store.HasRepPrefix ||
                store.HasRepnePrefix ||
                store.SegmentPrefix != NativeRegister.None ||
                store.MemoryBase != receiverRegister ||
                store.MemoryIndex != NativeRegister.None ||
                store.MemorySize.GetSize() != 1 ||
                store.MemoryDisplacement64 != (ulong)access.Offset ||
                store.Immediate8 != (value ? 1 : 0) ||
                unwind.ClassifySpan(address, store.NextIP).Kind !=
                    X64UnwindProof.SpanKind.HandlerFree)
                return false;
            var offset = checked((int)(address - caller.UnderlyingPointer));
            return offset <= caller.RawBytes.Length - store.Length &&
                   X64AncestorConstructorThunkProof.FileBackedExecutable(pe,
                       unwind, caller.RawBytes.AsSpan().Slice(offset,
                           store.Length), address);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }
}
