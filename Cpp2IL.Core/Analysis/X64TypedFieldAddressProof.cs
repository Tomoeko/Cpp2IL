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
using LibCpp2IL.PE;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Binds a native pointer calculation to unchanged aggregate field storage.
/// Taking a managed field address checks its owner; native ADD/LEA does not.
/// Admission therefore also authenticates the first fault before call effects.
/// </summary>
internal static partial class X64TypedFieldAddressProof
{
    internal sealed record Site(ManagedInstruction Operation, LocalVariable Pointer,
        LocalVariable Owner, FieldAnalysisContext Field, NativeInstruction Native,
        ManagedInstruction[] Calls, MethodAnalysisContext[] Targets);

    internal sealed class InputState(IEnumerable<object> facts)
    {
        private readonly object[] _facts = facts.ToArray();
        internal bool Matches(InputState other) => _facts.SequenceEqual(other._facts);
    }

    internal sealed record Proof(Site[] Sites, InputState Input);

    internal static Proof? Find(MethodAnalysisContext method, bool projected)
    {
        try
        {
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
                method.AppContext.Binary is not PE || method.ControlFlowGraph is not { } graph ||
                graph.EntryBlock.Instructions.Count != 0 || graph.ExitBlock.Instructions.Count != 0 ||
                method.IsStatic || method.IsVirtual || method.DeclaringType is not { } owner ||
                !OrdinaryClass(owner) || !OrdinaryMethod(method) ||
                new X64CallingConventionResolver().ReturnsViaHiddenBuffer(method))
                return null;
            var operations = graph.Instructions.ToArray();
            var candidates = operations.Where(operation => projected
                ? operation.Operands is [LocalVariable { Type: ByRefTypeAnalysisContext }, AddressOf { Target: FieldReference }]
                : operation is { OpCode: ISIL.OpCode.Add, IntegerBitWidth: 64,
                    Operands: [LocalVariable, LocalVariable, Immediate] }).ToArray();
            if (candidates.Length == 0 || !IncomingOwner(method, out var incoming))
                return null;
            if (ReadBody(method) is not { Length: > 0 and <= 96 } body ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null ||
                X64NativeInvocationValues.Create(body, new HashSet<ulong>()) is not { } values)
                return null;
            var facts = new List<object>();
            Capture(method, body, facts);
            var sites = new List<Site>();
            var boundCalls = new HashSet<ManagedInstruction>();
            foreach (var operation in candidates)
            {
                LocalVariable pointer;
                LocalVariable receiver;
                FieldAnalysisContext? field;
                if (projected)
                {
                    if (operation is not { OpCode: ISIL.OpCode.Move, IntegerBitWidth: 0,
                            Operands: [LocalVariable destination, AddressOf { Target: FieldReference addressed }] })
                        return null;
                    pointer = destination;
                    receiver = addressed.Local;
                    field = addressed.Field;
                    if (addressed.Offset != field.Offset || pointer.Type is not ByRefTypeAnalysisContext byref ||
                        !ReferenceEquals(byref.ElementType, field.FieldType)) return null;
                }
                else
                {
                    pointer = (LocalVariable)operation.Operands[0];
                    receiver = (LocalVariable)operation.Operands[1];
                    var offset = ((Immediate)operation.Operands[2]).Value;
                    var fields = owner.Fields.Where(candidate => !candidate.IsStatic && candidate.Offset == offset).ToArray();
                    field = fields is [var found] ? found : null;
                }
                if (field == null || field.BackingData?.Field.RawFieldType is not { Data: not null } ||
                    !ReferenceEquals(receiver, incoming) ||
                    operation.CallSemantics != CallSemantics.Direct || operation.NativeAddress is not { } address ||
                    body.SingleOrDefault(native => native.IP == address) is not { Length: > 0 } calculation ||
                    !AddressCalculation(calculation, out var destinationRegister, out var sourceRegister, out var displacement) ||
                    displacement != field.Offset ||
                    !values.Matches(address, sourceRegister, 64, new(NativeRegister.RCX)) ||
                    pointer.Register.Copy() != new ManagedRegister(null, X86Utils.GetRegisterName(destinationRegister)) ||
                    !Aggregate(field.FieldType, out var size) ||
                    !NullAddressMustFault(field.Offset, size) ||
                    !NarrowFieldEqualityProof.HasUnchangedAggregateFieldLayout(new FieldReference(field, receiver, field.Offset), size) ||
                    field.Name != field.DefaultName || (field.Attributes & FieldAttributes.InitOnly) != 0 ||
                    field.BackingData?.Field.RawFieldType is not
                        { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE, NumMods: 0, Byref: 0, Pinned: 0 } ||
                    operations.Count(instruction => ReferenceEquals(instruction.Destination, pointer)) != 1 ||
                    method.ParameterLocals.Contains(pointer) || pointer.IsThis || pointer.IsMethodInfo)
                    return null;
                var uses = operations.Where(instruction => OperandEffects.ReadLocals(instruction)
                    .Any(local => ReferenceEquals(local, pointer))).ToArray();
                if (uses.Length is 0 or > 8) return null;
                var targets = new List<MethodAnalysisContext>();
                foreach (var call in uses)
                {
                    if (!call.IsCall || call.CallSemantics != CallSemantics.Direct ||
                        call.NativeAddress is not { } callAddress ||
                        body.SingleOrDefault(native => native.IP == callAddress) is not { Length: > 0 } nativeCall ||
                        nativeCall.Code is not (Code.Call_rel32_64 or Code.Jmp_rel32_64) ||
                        !values.Dominates(address, callAddress) ||
                        !ManagedDominates(method, operation, call) ||
                        !values.Matches(callAddress, NativeRegister.RCX, 64,
                            new(NativeRegister.None, address, destinationRegister)) ||
                        !values.HasCallFrame(callAddress, nativeCall.Code == Code.Jmp_rel32_64) ||
                        Target(method, field.FieldType, nativeCall.NearBranchTarget, call, projected) is not { } target ||
                        !Arguments(method, target, call, pointer, nativeCall, values, projected) ||
                        ReadBody(target) is not { } callee ||
                        !FirstReceiverFault(target, callee, size) ||
                        !SafeBetween(body, calculation.IP, nativeCall.IP, uses, values))
                        return null;
                    targets.Add(target);
                    boundCalls.Add(call);
                    Capture(target, callee, facts);
                    facts.AddRange(method.AppContext.MethodsByAddress[target.UnderlyingPointer]);
                }
                for (var index = 1; index < uses.Length; index++)
                    if (uses[index - 1].NativeAddress >= uses[index].NativeAddress ||
                        !ManagedDominates(method, uses[index - 1], uses[index])) return null;
                X64SmallAggregateFieldGetterProof.CaptureType(field.FieldType, facts);
                sites.Add(new(operation, pointer, receiver, field, calculation, uses, targets.ToArray()));
            }
            // This bounded family contains only addressed calls, unchanged field
            // captures/guards, returns and ABI/frame setup. Unknown memory effects
            // cannot disappear merely because their register result is unused.
            if (operations.Any(instruction => instruction.IsCall && !boundCalls.Contains(instruction)) ||
                body.Any(native => !AllowedCallerInstruction(native, sites, boundCalls)) ||
                !GuardAndReturns(method, body, operations, sites, projected))
                return null;
            return new(sites.ToArray(), new InputState(facts));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static bool OrdinaryClass(TypeAnalysisContext type) =>
        type.Definition is { RawType: { Data: not null } } &&
        type.Fields.All(field => field.BackingData?.Field.RawFieldType is { Data: not null }) &&
        CompleteProjection(type) && NullCheckedCall.IsReferenceClass(type) && type is not GenericInstanceTypeAnalysisContext &&
        type.GenericParameters.Count == 0 && type.Name == type.DefaultName && type.Namespace == type.DefaultNamespace &&
        type.DeclaringType == null &&
        type.Attributes == type.DefaultAttributes && ReferenceEquals(type.BaseType, type.DefaultBaseType) &&
        type.Definition is { DeclaringTypeIndex: { IsNull: true }, GenericContainer: null, PackingSizeIsDefault: true, ClassSizeIsDefault: true,
            RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0 } };

    private static bool Aggregate(TypeAnalysisContext type, out int size)
    {
        size = 0;
        if (type.Definition is not { RawType: { Data: not null } } || !CompleteProjection(type) ||
            type.Fields.Any(field => field.BackingData?.Field.RawFieldType is not { Data: not null }))
            return false;
        size = checked((int)TypeSizes.UnboxedSize(type, 8));
        return size is 4 or 8 && type is not GenericInstanceTypeAnalysisContext &&
               type.DeclaringType == null &&
               type.Name == type.DefaultName && type.Namespace == type.DefaultNamespace &&
               type.Attributes == type.DefaultAttributes &&
               (type.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.SequentialLayout &&
               ReferenceEquals(type.BaseType, type.AppContext.SystemTypes.SystemValueTypeType) &&
               ReferenceEquals(type.BaseType, type.DefaultBaseType) && type.GenericParameters.Count == 0 &&
               type.Definition is { DeclaringTypeIndex: { IsNull: true }, IsValueType: true, IsEnumType: false, IsBlittable: true,
                   IsByRefLike: false, HasCctor: false, IsImportOrWindowsRuntime: false,
                   PackingSizeIsDefault: true, ClassSizeIsDefault: true,
                   RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE, NumMods: 0, Byref: 0, Pinned: 0 } } definition &&
               definition.RawSizes.native_size == size && type.Fields.Count == definition.FieldCount &&
               type.Fields.Where(field => !field.IsStatic).ToArray() is [var field] &&
               field.Name == field.DefaultName && field.Attributes == field.DefaultAttributes &&
               (field.Attributes & (FieldAttributes.Literal | FieldAttributes.InitOnly | FieldAttributes.HasFieldMarshal)) == 0 &&
               field.Offset == 0 && field.DefaultOffset == 0 && field.OverrideFieldType == null &&
               ReferenceEquals(field.BackingData?.Field.DeclaringType, definition) &&
               field.BackingData?.Field.RawFieldType is { Data: not null, NumMods: 0, Byref: 0, Pinned: 0 } raw &&
               raw.Type == field.FieldType.Type &&
               (size == 4 && field.FieldType.Type is Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 ||
                size == 8 && field.FieldType.Type is Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 or
                    Il2CppTypeEnum.IL2CPP_TYPE_I or Il2CppTypeEnum.IL2CPP_TYPE_U);
    }

    private static bool OrdinaryMethod(MethodAnalysisContext method, bool allowEnumArgument = false) =>
        method.Definition is { RawReturnType: { Data: not null } } &&
        method.Parameters.All(parameter => parameter.Definition?.RawType is { Data: not null }) &&
        !method.IsStatic && !method.IsVirtual && method.Name is not (".ctor" or ".cctor") &&
        method.Name == method.DefaultName && method.Attributes == method.DefaultAttributes &&
        method.ImplAttributes == method.DefaultImplAttributes && method.GenericParameters.Count == 0 &&
        method.OverrideReturnType == null && ReferenceEquals(method.ReturnType, method.DefaultReturnType) &&
        method.BaseMethod == null && method.Overrides.Count == 0 &&
        (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) == 0 &&
        (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                                  MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) == 0 &&
        method.Definition is { GenericContainer: null, RawReturnType: { NumMods: 0, Byref: 0, Pinned: 0 } } &&
        method.Parameters.Count <= 2 && method.Parameters.All(parameter =>
            parameter.OverrideParameterType == null && !parameter.IsRef &&
            parameter.Name == parameter.DefaultName && parameter.Attributes == parameter.DefaultAttributes &&
            !parameter.UseOverrideDefaultValue && ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) &&
            parameter.Definition?.RawType is { NumMods: 0, Byref: 0, Pinned: 0 } &&
            (parameter.ParameterType.Type is Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 or
                Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 or
                Il2CppTypeEnum.IL2CPP_TYPE_I or Il2CppTypeEnum.IL2CPP_TYPE_U ||
             allowEnumArgument && IsSignedEnum32(parameter.ParameterType))) &&
        RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) &&
        !RuntimeNullGuardCoalescer.HasOutputOptions(method);

    private static bool CompleteProjection(TypeAnalysisContext type) => type.Definition is { } definition &&
        type.Fields.Count == definition.FieldCount && type.Fields.Select(field => field.BackingData?.Field)
            .SequenceEqual(definition.Fields!) &&
        type.Methods.Count == definition.MethodCount && type.Methods.Select(method => method.Definition)
            .SequenceEqual(definition.Methods!);

    private static bool IncomingOwner(MethodAnalysisContext method, out LocalVariable owner)
    {
        owner = null!;
        var abi = new X64CallingConventionResolver().ResolveForParameters(method);
        if (abi.Length != method.ParameterOperands.Count || abi.Length != method.Parameters.Count + 2 ||
            !abi.SequenceEqual(method.ParameterOperands) || abi[0] is not ManagedRegister { Name: "rcx", Version: -1 } original ||
            method.ParameterLocals.Where(local => local.IsThis).ToArray() is not [var incoming] ||
            incoming.Register != original || !ReferenceEquals(incoming.Type, method.DeclaringType) ||
            method.ControlFlowGraph!.Instructions.Any(instruction => ReferenceEquals(instruction.Destination, incoming)))
            return false;
        owner = incoming;
        return true;
    }

    internal static bool AddressCalculation(NativeInstruction native, out NativeRegister destination,
        out NativeRegister source, out long offset)
    {
        destination = source = NativeRegister.None;
        offset = 0;
        if (native.Op0Kind != OpKind.Register || native.Op0Register.GetSize() != 8 ||
            native.Op0Register is NativeRegister.RSP or NativeRegister.RBP)
            return false;
        destination = native.Op0Register;
        if (native.Code is Code.Add_rm64_imm8 or Code.Add_rm64_imm32 &&
            native.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64)
        {
            source = destination;
            offset = unchecked((long)native.GetImmediate(1));
        }
        else if (native.Code == Code.Lea_r64_m && native.Op1Kind == OpKind.Memory &&
                 native.MemoryIndex == NativeRegister.None && native.MemoryIndexScale == 1 &&
                 native.MemoryBase is >= NativeRegister.RAX and <= NativeRegister.R15 &&
                 native.MemoryBase is not (NativeRegister.RSP or NativeRegister.RBP))
        {
            source = native.MemoryBase;
            offset = unchecked((long)native.MemoryDisplacement64);
        }
        return source != NativeRegister.None && offset is >= 16 and <= int.MaxValue - 8;
    }

    // Windows reserves the low 64 KiB for invalid/null-pointer accesses. Keep
    // the entire addressed storage below it, rather than inferring a fault for
    // an arbitrary positive pointer displacement.
    // https://learn.microsoft.com/en-us/shows/inside/access-violation-c0000005-read-or-write
    internal static bool NullAddressMustFault(long offset, int size)
        => offset >= 16 && size is 4 or 8 && offset <= 0x10000 - size;

    private static MethodAnalysisContext? Target(MethodAnalysisContext caller, TypeAnalysisContext aggregate,
        ulong address, ManagedInstruction call, bool projected)
    {
        // Even a different managed signature can share the observed native ABI
        // after casts or dead results disappear. Do not filter those identities
        // away using types that were inferred from an earlier guessed binding.
        var candidates = aggregate.Methods.Where(method => method.UnderlyingPointer == address).ToArray();
        if (candidates is not [var unique]) return null;
        var discardedEnum = caller.IsVoid && caller.Parameters.Count == 0 &&
                            unique.Definition?.RawReturnType is { Data: not null } &&
                            IsSignedEnum32(unique.ReturnType);
        if (!OrdinaryMethod(unique, discardedEnum) ||
            new X64CallingConventionResolver().ReturnsViaHiddenBuffer(unique)) return null;
        // Native sharing alone is ambiguous. The exact addressed managed owner,
        // argument count/types and return declaration must leave one identity.
        if (discardedEnum)
        {
            if (unique.Parameters.Count > 1 ||
                unique.Parameters.Count == 1 && !IsSignedEnum32(unique.Parameters[0].ParameterType) ||
                call.Operands[0] is MethodAnalysisContext && !HasUnusedEnumResult(caller, unique, call, projected))
                return null;
        }
        else if (unique.Parameters.Count != caller.Parameters.Count || !ReferenceEquals(unique.ReturnType, caller.ReturnType) ||
                 !unique.Parameters.Select(parameter => parameter.ParameterType)
                     .SequenceEqual(caller.Parameters.Select(parameter => parameter.ParameterType))) return null;
        if (call.Operands[0] is MethodAnalysisContext bound)
            return ReferenceEquals(unique, bound) ? unique : null;
        return call.Operands[0] is Immediate raw && unchecked((ulong)raw.Value) == address ? unique : null;
    }

    private static bool Arguments(MethodAnalysisContext caller, MethodAnalysisContext target,
        ManagedInstruction call, LocalVariable pointer, NativeInstruction native,
        X64NativeInvocationValues values, bool projected)
    {
        var abi = new X64CallingConventionResolver().ResolveForParameters(target);
        if (abi.Length != target.Parameters.Count + 2 || abi[0] is not ManagedRegister { Name: "rcx" } ||
            abi[^1] is not ManagedRegister metadata ||
            !values.Matches(native.IP, Native(metadata), 64, new(NativeRegister.None, Literal: 0)))
            return false;
        if (call.Operands[0] is not MethodAnalysisContext)
            return !projected && call.OpCode == ISIL.OpCode.CallVoid && call.Operands.Count >= 2 &&
                   ReferenceEquals(call.Operands[1], pointer) && target.Parameters.Count == 0;
        var discardedEnum = HasUnusedEnumResult(caller, target, call, projected);
        var first = call.OpCode == ISIL.OpCode.Call ? 2 : 1;
        if (call.OpCode != (target.IsVoid || discardedEnum && projected ? ISIL.OpCode.CallVoid : ISIL.OpCode.Call) ||
            call.Operands.Count != first + target.Parameters.Count + 1 ||
            !ReferenceEquals(call.Operands[first], pointer) ||
            call.OpCode == ISIL.OpCode.Call && call.Operands[1] is not LocalVariable { Type: not null } ||
            call.OpCode == ISIL.OpCode.Call && !ReferenceEquals(((LocalVariable)call.Operands[1]).Type, target.ReturnType))
            return false;
        for (var index = 0; index < target.Parameters.Count; index++)
        {
            var type = target.Parameters[index].ParameterType;
            var enumArgument = IsSignedEnum32(type);
            var bits = enumArgument || type.Type is Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 ? 32 : 64;
            if (abi[index + 1] is not ManagedRegister register) return false;
            var argument = call.Operands[first + index + 1];
            if (argument is Immediate literal)
            {
                if (enumArgument && (!discardedEnum || literal.Value != 0)) return false;
                if (!values.Matches(native.IP, Native(register), bits,
                        new(NativeRegister.None, Literal: unchecked((ulong)literal.Value)))) return false;
            }
            else if (!enumArgument && argument is LocalVariable local && caller.ParameterLocals.Contains(local) &&
                     LocalVariables.GetIncomingParameterIndex(caller, local) is { } parameterIndex &&
                     ReferenceEquals(caller.Parameters[parameterIndex].ParameterType, type) &&
                     ReferenceEquals(local.Type, type) && local.Register.Copy().Name is { } name &&
                     !caller.ControlFlowGraph!.Instructions.Any(instruction => ReferenceEquals(instruction.Destination, local)))
            {
                var entry = (NativeRegister)Enum.Parse(typeof(NativeRegister), name, true);
                if (!values.Matches(native.IP, Native(register), bits, new(entry))) return false;
            }
            else return false;
        }
        return true;
    }

    private static NativeRegister Native(ManagedRegister register)
        => (NativeRegister)Enum.Parse(typeof(NativeRegister), register.Name, true);

    internal static NativeInstruction[]? ReadBody(MethodAnalysisContext method)
    {
        if (method.RawBytes.Length == 0)
            method.EnsureRawBytes();
        var rooted = X64NativeInstructionReader.ReadRootBody(method);
        if (rooted != null) return rooted;
        var cached = X86Utils.Iterate(method).Take(97).ToArray();
        return cached.Length is > 0 and <= 96 && cached[^1].NextIP - method.UnderlyingPointer <= 4096
            ? X64NativeInstructionReader.ReadFramelessBody(method, cached.Length,
                checked((int)(cached[^1].NextIP - method.UnderlyingPointer))) : null;
    }

    internal static bool FirstReceiverFault(MethodAnalysisContext target, NativeInstruction[] body, int size)
    {
        if (X86CallerExceptionRegionProof.Check(target, body, new HashSet<ulong>()) != null ||
            X64NativeInvocationValues.Create(body, new HashSet<ulong>()) is not { } values ||
            target.AppContext.Binary is not PE pe)
            return false;
        foreach (var native in body)
        {
            if (native.FlowControl != FlowControl.Next) return false;
            var memory = new InstructionInfoFactory().GetInfo(native).GetUsedMemory().ToArray();
            foreach (var access in memory)
            {
                if (access.Base == NativeRegister.RSP && access.Index == NativeRegister.None)
                    continue; // Owned stack frame/spill; unwind proof is separate.
                if (native.IsIPRelativeMemoryOperand && native.Code == Code.Mov_r64_rm64)
                {
                    var raw = pe.MapVirtualAddressToRaw(native.IPRelativeMemoryAddress, false);
                    if (raw < 0 || raw > pe.GetRawBinaryContent().Length - 8) return false;
                    continue; // A mapped runtime cache read has no receiver fault or write.
                }
                if (access.Base is >= NativeRegister.RAX and <= NativeRegister.R15 &&
                    access.Index == NativeRegister.None && access.Displacement < (ulong)size &&
                    access.Displacement + (uint)access.MemorySize.GetSize() <= (ulong)size &&
                    values.Matches(native.IP, access.Base, 64, new(NativeRegister.RCX)) &&
                    native.Mnemonic is Mnemonic.Mov or Mnemonic.Add or Mnemonic.Inc or Mnemonic.Dec)
                    return true;
                return false;
            }
            if (memory.Length == 0 && !FrameOrPureRegister(native)) return false;
        }
        return false;
    }

    private static bool SafeBetween(NativeInstruction[] body, ulong calculation, ulong call,
        ManagedInstruction[] sameAddressCalls, X64NativeInvocationValues values)
        => body.Where(native => native.IP > calculation && native.IP < call)
            .All(native => FrameOrPureRegister(native) ||
                native.Code == Code.Call_rel32_64 && sameAddressCalls.Any(earlier =>
                    earlier.NativeAddress == native.IP && values.Dominates(native.IP, call)));
    // An admitted preceding call consumes this exact address and has the same
    // receiver-first-fault proof. Dominance establishes that it already faulted
    // before any effects on every path reaching the subsequent call.

    private static bool FrameOrPureRegister(NativeInstruction native)
    {
        if (native.Code is Code.Push_r64 or Code.Pop_r64) return true;
        if (native.Code is Code.Sub_rm64_imm8 or Code.Add_rm64_imm8 &&
            native.Op0Kind == OpKind.Register && native.Op0Register == NativeRegister.RSP) return true;
        if (native.Code is Code.Mov_rm64_r64 or Code.Mov_r64_rm64 &&
            native.MemoryBase == NativeRegister.RSP && native.MemoryIndex == NativeRegister.None) return true;
        return native.Mnemonic is Mnemonic.Mov or Mnemonic.Xor && native.Op0Kind == OpKind.Register &&
               native.Op1Kind == OpKind.Register;
    }

    private static bool AllowedCallerInstruction(NativeInstruction native, List<Site> sites,
        HashSet<ManagedInstruction> calls)
        => sites.Any(site => site.Native.IP == native.IP) || calls.Any(call => call.NativeAddress == native.IP) ||
           FrameOrPureRegister(native) || native.Code == Code.Retnq ||
           native.Code is Code.Cmp_rm8_imm8 or Code.Je_rel8_64 or Code.Jne_rel8_64;

    private static bool GuardAndReturns(MethodAnalysisContext method, NativeInstruction[] body,
        ManagedInstruction[] operations, List<Site> sites, bool projected)
    {
        var permitted = new HashSet<ManagedInstruction>(sites.Select(site => site.Operation)
            .Concat(sites.SelectMany(site => site.Calls)));
        var first = method.ControlFlowGraph!.Blocks.FirstOrDefault(block => block.Instructions.Count != 0)
            ?.Instructions.FirstOrDefault();
        var earliest = operations.Where(operation => operation.OpCode != ISIL.OpCode.Nop)
            .Min(operation => operation.NativeAddress ?? ulong.MaxValue);
        if (first?.NativeAddress != earliest) return false;
        if (body.Any(native => native.Code == Code.Cmp_rm8_imm8))
        {
            if (!Guard(method, body, operations, sites, permitted)) return false;
        }
        foreach (var operation in operations)
        {
            if (permitted.Contains(operation)) continue;
            if (operation.OpCode == ISIL.OpCode.Nop && operation.Operands.Count == 0 && operation.IntegerBitWidth == 0) continue;
            if (!projected && operation.OpCode == ISIL.OpCode.UnresolvedValue &&
                sites.Any(site => site.Calls.Any(call => call.Operands[0] is Immediate)) &&
                operation.Operands is [LocalVariable value, StringLiteral] && ReferenceEquals(value.Type, method.ReturnType)) continue;
            if (operation.OpCode == ISIL.OpCode.Return && Return(method, body, operation, sites, projected)) continue;
            return false;
        }
        // Pointer ADD flags may be discarded only when no later branch consumes
        // them. A guarded family branches before forming the address.
        return sites.All(site => body.Where(native => native.IP > site.Native.IP)
                   .All(native => native.FlowControl != FlowControl.ConditionalBranch)) &&
               body.Where(native => native.Code == Code.Retnq || native.Code == Code.Jmp_rel32_64)
                   .All(native => operations.Count(operation => operation.OpCode == ISIL.OpCode.Return &&
                       operation.NativeAddress == native.IP) == 1);
    }

    private static bool Return(MethodAnalysisContext method, NativeInstruction[] body,
        ManagedInstruction operation, List<Site> sites, bool projected)
    {
        if (operation.CallSemantics != CallSemantics.Direct || operation.IntegerBitWidth != 0 ||
            operation.NativeAddress is not { } address ||
            method.ControlFlowGraph!.Blocks.Count(block => ReferenceEquals(block.Instructions.LastOrDefault(), operation)) != 1)
            return false;
        var tails = sites.SelectMany(site => site.Calls.Select((call, index) => (call, target: site.Targets[index])))
            .Where(pair => pair.call.NativeAddress == address &&
                body.Any(native => native.IP == address && native.Code == Code.Jmp_rel32_64)).ToArray();
        if (tails is [(var call, var target)])
        {
            if (!ManagedDominates(method, call, operation)) return false;
            if (method.IsVoid) return (target.IsVoid || HasUnusedEnumResult(method, target, call, projected)) &&
                                      operation.Operands.Count == 0;
            if (!ReferenceEquals(method.ReturnType, target.ReturnType) ||
                operation.Operands is not [LocalVariable result] ||
                !ReferenceEquals(result.Type, method.ReturnType)) return false;
            if (call.OpCode == ISIL.OpCode.Call)
                return ReferenceEquals(call.Operands[1], result);
            return !projected && call.Operands[0] is Immediate &&
                   method.ControlFlowGraph!.Instructions.SingleOrDefault(instruction =>
                       ReferenceEquals(instruction.Destination, result)) is
                       { OpCode: ISIL.OpCode.UnresolvedValue, Operands: [_, StringLiteral] };
        }
        var index = Array.FindIndex(body, native => native.IP == address && native.Code == Code.Retnq);
        if (method.IsVoid && operation.Operands.Count == 0 && index >= 0)
        {
            // The guarded no-op path reaches its exact native RET without any
            // call. Otherwise every admitted call must precede this return.
            if (body.Length > 1 && body[0].Code == Code.Cmp_rm8_imm8 &&
                body[1].Code is Code.Je_rel8_64 or Code.Jne_rel8_64 && body[1].NearBranchTarget == address)
                return method.ControlFlowGraph!.Blocks.Single(block => block.Instructions.Contains(operation))
                    .Instructions.Count == 1;
            return sites.SelectMany(site => site.Calls).All(call => ManagedDominates(method, call, operation));
        }
        return index > 0 && body[index - 1].Mnemonic == Mnemonic.Xor &&
               body[index - 1].Op0Kind == OpKind.Register && body[index - 1].Op1Kind == OpKind.Register &&
               body[index - 1].Op0Register == NativeRegister.EAX && body[index - 1].Op1Register == NativeRegister.EAX &&
               operation.Operands is [Immediate { Value: 0 }] && !method.IsVoid;
    }

    private static bool Guard(MethodAnalysisContext method, NativeInstruction[] body,
        ManagedInstruction[] operations, List<Site> sites, HashSet<ManagedInstruction> permitted)
    {
        if (body.Length < 5 || body[0].Code != Code.Cmp_rm8_imm8 || body[0].MemoryBase != NativeRegister.RCX ||
            body[0].MemoryIndex != NativeRegister.None || body[0].Immediate8 != 0 ||
            body[1].Code is not (Code.Je_rel8_64 or Code.Jne_rel8_64) ||
            operations.Where(operation => operation.NativeAddress == body[0].IP).ToArray() is not
                [{ OpCode: ISIL.OpCode.Move, IntegerBitWidth: 8,
                    Operands: [LocalVariable captured, FieldReference field] } capture,
                 { OpCode: ISIL.OpCode.CheckEqual, IntegerBitWidth: 8,
                    Operands: [LocalVariable equal, LocalVariable input, Immediate { Value: 0 }] } compare] ||
            !ReferenceEquals(input, captured) || !ReferenceEquals(field.Local, sites[0].Owner) ||
            body[0].MemoryDisplacement64 != (ulong)field.Offset ||
            !ReferenceEquals(field.Field.FieldType, method.AppContext.SystemTypes.SystemBooleanType) ||
            !ReferenceEquals(captured.Type, field.Field.FieldType) ||
            !ReferenceEquals(equal.Type, captured.Type) ||
            !NarrowFieldEqualityProof.HasUnchangedByteFieldLayout(field) ||
            operations.Where(operation => operation.OpCode == ISIL.OpCode.ConditionalJump).ToArray() is not
                [{ Operands: [var target, LocalVariable condition] } branch] ||
            branch.NativeAddress != body[1].IP)
            return false;
        var graph = method.ControlFlowGraph!;
        var targetBlock = target switch
        {
            Graphs.Block block => block,
            ManagedInstruction instruction => graph.Blocks.SingleOrDefault(block =>
                block.Instructions.FirstOrDefault() == instruction),
            _ => null
        };
        var guard = graph.Blocks.SingleOrDefault(block => block.Instructions.Contains(branch));
        var fallthrough = body.Skip(2).Select(native => native.IP)
            .FirstOrDefault(address => operations.Any(operation => operation.NativeAddress == address));
        var falseBlock = graph.Blocks.SingleOrDefault(block => block.Instructions.FirstOrDefault()?.NativeAddress == fallthrough);
        if (targetBlock == null || targetBlock.Instructions.FirstOrDefault()?.NativeAddress != body[1].NearBranchTarget ||
            guard == null || !ReferenceEquals(graph.Blocks.FirstOrDefault(block => block.Instructions.Count != 0), guard) ||
            guard.Successors.Count != 2 || !guard.Successors.Contains(targetBlock) ||
            falseBlock == null || ReferenceEquals(falseBlock, targetBlock) || !guard.Successors.Contains(falseBlock) ||
            !ReferenceEquals(guard.Instructions.LastOrDefault(), branch))
            return false;
        if (body[1].Code == Code.Je_rel8_64)
        {
            if (!ReferenceEquals(condition, equal)) return false;
        }
        else
        {
            if (operations.Where(operation => operation.OpCode == ISIL.OpCode.Not).ToArray() is not
                [{ Operands: [LocalVariable negated, LocalVariable original] } invert] ||
                !ReferenceEquals(condition, negated) || !ReferenceEquals(original, equal) ||
                !ReferenceEquals(negated.Type, equal.Type) || invert.NativeAddress != body[1].IP)
                return false;
            permitted.Add(invert);
        }
        permitted.Add(capture);
        permitted.Add(compare);
        permitted.Add(branch);
        return guard.Instructions.SequenceEqual(body[1].Code == Code.Je_rel8_64
            ? new[] { capture, compare, branch }
            : new[] { capture, compare, permitted.Single(operation => operation.OpCode == ISIL.OpCode.Not), branch });
    }

    private static bool ManagedDominates(MethodAnalysisContext method, ManagedInstruction definition,
        ManagedInstruction use)
    {
        var graph = method.ControlFlowGraph!;
        var sources = graph.Blocks.Where(block => block.Instructions.Contains(definition)).ToArray();
        var destinations = graph.Blocks.Where(block => block.Instructions.Contains(use)).ToArray();
        if (sources is not [var source] || destinations is not [var destination]) return false;
        if (ReferenceEquals(source, destination))
            return source.Instructions.IndexOf(definition) < source.Instructions.IndexOf(use);
        var visited = new HashSet<Graphs.Block>();
        var pending = new Stack<Graphs.Block>();
        pending.Push(destination);
        while (pending.Count != 0)
        {
            var block = pending.Pop();
            if (ReferenceEquals(block, source)) continue;
            if (ReferenceEquals(block, graph.EntryBlock) || !graph.Blocks.Contains(block) || block.Predecessors.Count == 0)
                return false;
            if (!visited.Add(block)) continue;
            foreach (var previous in block.Predecessors)
            {
                if (!previous.Successors.Contains(block)) return false;
                pending.Push(previous);
            }
        }
        return true;
    }

    private static void Capture(MethodAnalysisContext method, NativeInstruction[] body, List<object> facts)
    {
        var visited = new HashSet<TypeAnalysisContext>();
        for (var type = method.DeclaringType; type != null; type = type.BaseType)
        {
            if (!visited.Add(type) || type.Definition is not { RawType: { Data: not null } } ||
                type.Fields.Any(field => field.BackingData?.Field.RawFieldType is not { Data: not null }))
                throw new InvalidOperationException("Incomplete field owner ancestry");
            X64SmallAggregateFieldGetterProof.CaptureType(type, facts);
            foreach (var member in type.Methods)
            {
                facts.Add(member);
                facts.Add(member.Definition!);
                facts.Add(member.Name);
                facts.Add(member.Attributes);
                facts.Add(member.ImplAttributes);
                facts.Add(member.UnderlyingPointer);
                facts.Add(member.Definition!.returnTypeIdx);
                facts.Add(member.Definition.parameterStart);
                facts.Add(member.Definition.parameterCount);
            }
        }
        X64SmallAggregateFieldGetterProof.CaptureMethod(method, facts);
        X64SmallAggregateFieldGetterProof.CaptureRawType(method.Definition!.RawReturnType!, facts);
        if (method.ReturnType.IsEnumType) CaptureSignedEnum32(method.ReturnType, facts);
        foreach (var parameter in method.Parameters)
        {
            facts.Add(parameter);
            facts.Add(parameter.Name);
            facts.Add(parameter.Attributes);
            facts.Add(parameter.ParameterType);
            X64SmallAggregateFieldGetterProof.CaptureRawType(parameter.Definition!.RawType!, facts);
            if (parameter.ParameterType.IsEnumType) CaptureSignedEnum32(parameter.ParameterType, facts);
        }
        facts.AddRange(body.Cast<object>());
        var length = checked((int)(body[^1].NextIP - method.UnderlyingPointer));
        facts.AddRange(method.RawBytes.AsSpan().Slice(0, length).ToArray().Select(value => (object)value));
    }
}
