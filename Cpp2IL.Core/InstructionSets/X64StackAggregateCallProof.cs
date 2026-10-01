using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds a complete private twelve-byte argument copy to its original managed
/// by-value identity. It does not qualify callee behavior, hidden result transfer,
/// ref storage, caller helpers or the remaining caller operations.
/// </summary>
internal static partial class X64StackAggregateCallProof
{
    internal const string EvidenceKey = "X64StackAggregateCallProof.Arguments";
    internal const string SnapshotPrefix = "aggregate_argument_";
    internal sealed record Site(ArgumentCopy Copy, int SourceParameter, int TargetParameter,
        TypeAnalysisContext Type, MethodAnalysisContext Target, bool TargetHasHiddenResult);
    private sealed record Descriptor(Il2CppType Instance, ulong Address, ulong Data, uint Bits, string Bytes);

    internal sealed class Evidence
    {
        private readonly Instruction[] _body;
        private readonly Site[] _sites;
        private readonly object[] _bindings;
        internal MethodAnalysisContext Method { get; }
        internal ReadOnlySpan<Instruction> Body => _body;
        internal ReadOnlySpan<Site> Sites => _sites;

        internal Evidence(MethodAnalysisContext method, Instruction[] body, Site[] sites, object[] bindings)
        {
            Method = method;
            _body = body.ToArray(); _sites = sites.ToArray(); _bindings = bindings.ToArray();
        }

        internal bool IsUnchanged() => Find(Method) is { } current &&
            _body.SequenceEqual(current._body) && _sites.SequenceEqual(current._sites) && _bindings.SequenceEqual(current._bindings);
    }

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey) || method.GetExtraData<Evidence>(EvidenceKey) != null;

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            var bindings = new List<object>();
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.MetadataVersion != 29 ||
                app.Binary is not PE pe || !BindMethod(method, bindings) ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var originalAliases) || originalAliases is not [var original] ||
                !ReferenceEquals(original, method) || method.Parameters.All(parameter =>
                    parameter.Definition?.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE })) return null;
            if (method.RawBytes.Length == 0) method.EnsureRawBytes();
            // A fragment of a chained root is insufficient. This first recipe
            // requires the independent root reader to cover the supplied body.
            if (X64NativeInstructionReader.ReadRootBody(method) is not { Length: > 0 } body ||
                body.Length > 1024 || body.Any(instruction => !Clean(instruction)) ||
                X64UnwindProof.ForApplication(app) is not { } index) return null;
            var noReturnCalls = new HashSet<ulong>();
            foreach (var instruction in body.Where(instruction => instruction.Code == Code.Call_rel32_64))
                if (X86RuntimeNullThrowProof.TryIdentify(app, instruction.NearBranchTarget) != null)
                    noReturnCalls.Add(instruction.IP);
            if (X86CallerExceptionRegionProof.Check(method, body, noReturnCalls) != null) return null;
            var bodyOffset = pe.MapVirtualAddressToRaw(body[0].IP, false);
            var bodySize = checked((int)(body[^1].NextIP - body[0].IP));
            if (bodyOffset < 0 || bodyOffset > pe.GetRawBinaryContent().Length - bodySize ||
                !index.IsUnaffectedByBaseRelocation(body[0].IP, checked((uint)bodySize))) return null;
            bindings.Add(Convert.ToBase64String(pe.GetRawBinaryContent().Slice(checked((int)bodyOffset), bodySize).ToArray()));
            var calling = new X64CallingConventionResolver();
            var sources = new List<(ParameterAnalysisContext Parameter, Register Incoming)>();
            var callerArguments = calling.ResolveForManaged(method);
            foreach (var parameter in method.Parameters)
            {
                var slot = parameter.ParameterIndex + (method.IsStatic ? 0 : 1);
                if (parameter.IsRef || !BindAggregate(parameter.ParameterType, 12, bindings) ||
                    slot >= callerArguments.Length || callerArguments[slot] is not ISIL.Register register ||
                    !Enum.TryParse<Register>(register.Name, true, out var native) || native is not
                        (Register.RCX or Register.RDX or Register.R8 or Register.R9)) continue;
                sources.Add((parameter, native));
            }
            if (sources.Count == 0) return null;
            if (!UniqueOriginalNativeBinding(method, index, bindings, body[^1].NextIP)) return null;
            var sites = new List<Site>();
            foreach (var call in body.Where(instruction => instruction.Code == Code.Call_rel32_64))
            {
                if (!app.MethodsByAddress.TryGetValue(call.NearBranchTarget, out var aliases) || aliases is not [var target] ||
                    target.Parameters.All(parameter => parameter.Definition?.RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE })) continue;
                var targetBindings = new List<object>();
                if (!BindMethod(target, targetBindings) || target.Parameters.Count != 1 || !target.IsStatic && target.DeclaringType!.IsValueType ||
                    target.Parameters.Any(parameter => parameter.IsRef)) continue;
                var targetArguments = calling.ResolveForManaged(target);
                foreach (var parameter in target.Parameters)
                {
                    var slot = parameter.ParameterIndex + (target.IsStatic ? 0 : 1);
                    if (!BindAggregate(parameter.ParameterType, 12, targetBindings) || slot >= targetArguments.Length ||
                        targetArguments[slot] is not ISIL.Register register ||
                        !Enum.TryParse<Register>(register.Name, true, out var argument) ||
                        TryFindArgumentCopy(body, call.IP, argument, ResultRegister(target), ResultRegister(method),
                            ResultBytes(target), ResultBytes(method)) is not { } copy ||
                        !ZeroMethodInfo(body, copy, target, calling) ||
                        !DisjointCallBuffers(body, call.IP, copy, target, calling)) continue;
                    var matches = sources.Where(source => ReferenceEquals(source.Parameter.ParameterType, parameter.ParameterType) &&
                        HasUnescapedParameterOrigin(body, copy, source.Incoming, noReturnCalls)).ToArray();
                    if (matches is not [var source]) continue;
                    if (!UniqueOriginalNativeBinding(target, index, targetBindings, target.UnderlyingPointer + 1)) continue;
                    sites.Add(new(copy, source.Parameter.ParameterIndex, parameter.ParameterIndex, parameter.ParameterType,
                        target, calling.ReturnsViaHiddenBuffer(target)));
                    bindings.AddRange(targetBindings);
                }
            }
            if (sites.Count == 0 || sites.SelectMany(site => site.Copy.ReplacedAddresses.Append(site.Copy.Address))
                    .Distinct().Count() != sites.Count * 5) return null;
            return new(method, body, sites.ToArray(), bindings.ToArray());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or KeyNotFoundException or NullReferenceException)
        {
            return null;
        }
    }

    private static bool BindMethod(MethodAnalysisContext method, List<object> bindings)
    {
        var app = method.AppContext;
        if (method is ConcreteGenericMethodAnalysisContext || method.Definition is not { GenericContainer: null } definition ||
            method.DeclaringType is not { Definition: { GenericContainer: null } ownerDefinition } owner ||
            method.Name is ".ctor" or ".cctor" || method.IsVirtual || method.GenericParameters.Count != 0 ||
            owner.GenericParameters.Count != 0 || owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes || owner.OverrideBaseType != null ||
            !ReferenceEquals(definition.DeclaringType, ownerDefinition) ||
            !ReferenceEquals(app.ResolveContextForMethod(definition), method) ||
            !X64OriginalReferenceClassProof.OriginalType(app, ownerDefinition) ||
            !X64OriginalReferenceClassProof.OriginalMethod(app, definition) ||
            !X64OriginalReferenceClassProof.OriginalMethodPointer(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method) || RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            method.Name != method.DefaultName || method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes || method.OverrideReturnType != null ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) != 0 ||
            definition.InternalParameterData is not { } originals || originals.Length != method.Parameters.Count ||
            !BindDescriptor(app, definition.RawReturnType, bindings) ||
            !BindAbiType(method.ReturnType, definition.RawReturnType!, bindings)) return false;
        bindings.Add(method); bindings.Add(owner); bindings.Add(method.Name); bindings.Add(method.Attributes); bindings.Add(method.ImplAttributes);
        bindings.Add(owner.Name); bindings.Add(owner.Namespace); bindings.Add(owner.Attributes); bindings.Add(method.ReturnType);
        if (method.ReturnType.IsValueType && !method.IsVoid && !BaseCallingConventionResolver.IsFloatingPoint(method.ReturnType))
        {
            var size = TypeSizes.UnboxedSize(method.ReturnType, 8);
            if (size is not (1 or 2 or 4 or 8) && (size is not (12 or 16) || !BindAggregate(method.ReturnType, checked((int)size), bindings))) return false;
        }
        for (var ordinal = 0; ordinal < originals.Length; ordinal++)
        {
            var parameter = method.Parameters[ordinal];
            if (!ReferenceEquals(parameter.Definition, originals[ordinal]) || parameter.ParameterIndex != ordinal ||
                !ReferenceEquals(parameter.DeclaringMethod, method) || parameter.OverrideParameterType != null ||
                parameter.OverrideAttributes != null || parameter.Name != parameter.DefaultName ||
                parameter.Attributes != parameter.DefaultAttributes || parameter.UseOverrideDefaultValue ||
                !X64OriginalReferenceClassProof.OriginalParameter(app, definition, ordinal, originals[ordinal]) ||
                !BindDescriptor(app, originals[ordinal].RawType, bindings) ||
                !BindAbiType(parameter.ParameterType, originals[ordinal].RawType!, bindings)) return false;
            bindings.Add(parameter); bindings.Add(parameter.ParameterType); bindings.Add(parameter.Name); bindings.Add(parameter.Attributes);
        }
        return true;
    }

    private static bool BindAbiType(TypeAnalysisContext type, Il2CppType raw, List<object> bindings)
    {
        var app = type.AppContext;
        if (type.Definition is not { GenericContainer: null } definition || type.IsGenericInstance ||
            type.GenericParameters.Count != 0 || type.Name != type.DefaultName || type.Namespace != type.DefaultNamespace ||
            type.Attributes != type.DefaultAttributes || type.OverrideBaseType != null ||
            !X64OriginalReferenceClassProof.OriginalType(app, definition) ||
            !BindDescriptor(app, definition.RawType, bindings) || definition.RawType.Type != raw.Type ||
            raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE &&
                raw.Data.Dummy != (ulong)definition.TypeIndex.Value) return false;
        bindings.Add(type); bindings.Add(type.Name); bindings.Add(type.Namespace); bindings.Add(type.Attributes);
        bindings.Add(definition.Bitfield);
        if (raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_VOID || !type.IsValueType || BaseCallingConventionResolver.IsFloatingPoint(type)) return true;
        var size = TypeSizes.UnboxedSize(type, 8);
        if (size is 12 or 16) return BindAggregate(type, checked((int)size), bindings);
        if (size is not (1 or 2 or 4 or 8) ||
            !X64OriginalReferenceClassProof.OriginalInstanceFieldLayout(type, out var layout, unboxValueTypeOffsets: true)) return false;
        bindings.Add(Convert.ToBase64String(layout));
        return true;
    }

    private static bool BindAggregate(TypeAnalysisContext type, int size, List<object> bindings)
    {
        var app = type.AppContext;
        if (size is not (12 or 16) || type.Definition is not { IsValueType: true, IsEnumType: false,
                IsBlittable: true, IsImportOrWindowsRuntime: false, IsByRefLike: false, GenericContainer: null,
                PackingSizeIsDefault: true, ClassSizeIsDefault: true, DeclaringTypeIndex: { IsNull: true } } definition ||
            type.IsGenericInstance || type.GenericParameters.Count != 0 || type.DeclaringType != null ||
            type.Name != type.DefaultName || type.Namespace != type.DefaultNamespace || type.Attributes != type.DefaultAttributes ||
            type.OverrideBaseType != null || (type.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.SequentialLayout ||
            !X64OriginalReferenceClassProof.OriginalType(app, definition) ||
            !BindDescriptor(app, definition.RawType, bindings) || definition.RawType.Type != Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE ||
            definition.RawType.ValueType != 1 || definition.RawType.Data.Dummy != (ulong)definition.TypeIndex.Value ||
            !(definition.Fields ?? []).SequenceEqual(type.Fields.Select(field => field.BackingData?.Field)) ||
            !X64OriginalReferenceClassProof.OriginalInstanceFieldLayout(type, out var layout, unboxValueTypeOffsets: true) ||
            TypeSizes.UnboxedSize(type, 8) != size) return false;
        var fields = type.Fields.Where(field => !field.IsStatic).OrderBy(field => field.Offset).ToArray();
        if (fields.Length != size / 4) return false;
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (field.BackingData?.Field is not { } original || !ReferenceEquals(field.DeclaringType, type) ||
                !ReferenceEquals(original.DeclaringType, definition) || !X64OriginalReferenceClassProof.OriginalField(app, original) ||
                field.Offset != index * 4 || field.Offset != field.DefaultOffset || field.Name != field.DefaultName ||
                field.Attributes != field.DefaultAttributes || field.OverrideFieldType != null || field.UseOverrideConstantValue ||
                !ReferenceEquals(field.FieldType, app.SystemTypes.SystemSingleType) ||
                original.RawFieldType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_R4, Byref: 0, NumMods: 0, Pinned: 0 } raw ||
                !BindDescriptor(app, raw, bindings)) return false;
            bindings.Add(field); bindings.Add(field.Name); bindings.Add(field.Attributes); bindings.Add(field.Offset);
        }
        bindings.Add(type); bindings.Add(type.Name); bindings.Add(type.Namespace); bindings.Add(type.Attributes);
        bindings.Add(Convert.ToBase64String(layout));
        return true;
    }

    private static bool BindDescriptor(ApplicationAnalysisContext app, Il2CppType? raw, List<object> bindings)
    {
        if (raw?.Data == null || raw.Byref != 0 || raw.NumMods != 0 || raw.Pinned != 0 ||
            !X64OriginalReferenceClassProof.RetainedDescriptor(app, raw) ||
            raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE or Il2CppTypeEnum.IL2CPP_TYPE_CLASS &&
                raw.Data.Dummy >= (ulong)app.Metadata.TypeDefinitionCount ||
            raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST or Il2CppTypeEnum.IL2CPP_TYPE_VAR or Il2CppTypeEnum.IL2CPP_TYPE_MVAR ||
            raw.Type is Il2CppTypeEnum.IL2CPP_TYPE_PTR or Il2CppTypeEnum.IL2CPP_TYPE_FNPTR or Il2CppTypeEnum.IL2CPP_TYPE_TYPEDBYREF ||
            !app.Binary.TryGetTypeVirtualAddress(raw, out var address) || address > ulong.MaxValue - 11) return false;
        var offset = app.Binary.MapVirtualAddressToRaw(address, false);
        var bytes = app.Binary.GetRawBinaryContent();
        if (offset < 0 || offset > bytes.Length - 12 || app.Binary.MapVirtualAddressToRaw(address + 11, false) != offset + 11) return false;
        bindings.Add(new Descriptor(raw, address, raw.Data.Dummy, raw.Bits,
            Convert.ToBase64String(bytes.Slice(checked((int)offset), 12).ToArray())));
        return true;
    }

    private static bool ZeroMethodInfo(IReadOnlyList<Instruction> body, ArgumentCopy copy, MethodAnalysisContext target,
        X64CallingConventionResolver calling)
    {
        var arguments = calling.ResolveForManaged(target);
        var at = Index(body, copy.Call);
        if (at < 0 || arguments.Length == 0) return false;
        if (arguments[^1] is ISIL.Register register && Enum.TryParse<Register>(register.Name, true, out var native))
        {
            return HasZeroRegisterArgument(body, copy, native);
        }
        if (arguments[^1] is not ISIL.StackOffset stack) return false;
        var writes = body.Take(at).Where(instruction => instruction.Op0Kind == OpKind.Memory &&
            instruction.MemoryBase == Register.RSP && instruction.MemoryDisplacement64 == (ulong)stack.Offset).ToArray();
        return writes.LastOrDefault() is { Code: Code.Mov_rm64_imm32 } zero && zero.GetImmediate(1) == 0 &&
            zero.IP >= copy.FirstRead;
    }

    private static Register ResultRegister(MethodAnalysisContext method) => method.IsVoid ? Register.None :
        BaseCallingConventionResolver.IsFloatingPoint(method.ReturnType) ? Register.XMM0 : Register.RAX;

    private static int ResultBytes(MethodAnalysisContext method) => method.IsVoid ? 0 :
        ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemSingleType) ? 4 :
        ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemDoubleType) || !method.ReturnType.IsValueType ? 8 :
        checked((int)Math.Min(8, TypeSizes.UnboxedSize(method.ReturnType, 8)));

    private static bool DisjointCallBuffers(IReadOnlyList<Instruction> body, ulong call, ArgumentCopy copy,
        MethodAnalysisContext target, X64CallingConventionResolver calling)
    {
        var at = Index(body, call);
        var arguments = calling.ResolveForManaged(target);
        var ranges = new List<(Register Register, long Size)>();
        if (calling.ReturnsViaHiddenBuffer(target)) ranges.Add((Register.RCX, TypeSizes.UnboxedSize(target.ReturnType, 8)));
        foreach (var parameter in target.Parameters)
        {
            if (!parameter.ParameterType.IsValueType || BaseCallingConventionResolver.IsFloatingPoint(parameter.ParameterType)) continue;
            var size = TypeSizes.UnboxedSize(parameter.ParameterType, 8);
            if (size is 1 or 2 or 4 or 8) continue;
            var slot = parameter.ParameterIndex + (target.IsStatic ? 0 : 1);
            if (arguments[slot] is not ISIL.Register register || !Enum.TryParse<Register>(register.Name, true, out var native)) return false;
            ranges.Add((native, size));
        }
        foreach (var (register, size) in ranges.Where(range => range.Register != copy.Argument))
        {
            var last = LastWrite(body, at, register);
            if (last < 0 || body[last].IP < copy.FirstRead || size is not (12 or 16) || body[last] is not { Code: Code.Lea_r64_m } address ||
                !FrameMemory(address, copy.FrameSize, checked((uint)size)) ||
                address.MemoryDisplacement64 < copy.StackOffset + 12 && copy.StackOffset < address.MemoryDisplacement64 + (ulong)size)
                return false;
        }
        return true;
    }

    // A mutable alias cache cannot establish uniqueness. Enumerate every original
    // image's file-backed MethodDef pointer table and the complete original generic
    // registration before accepting this deliberately nongeneric singleton route.
    private static bool UniqueOriginalNativeBinding(MethodAnalysisContext method, X64UnwindProof.Index index, List<object> bindings, ulong end)
    {
        var app = method.AppContext;
        var pe = (PE)app.Binary;
        var pointer = method.UnderlyingPointer;
        var metadata = app.Metadata;
        if (end <= pointer || metadata.methodDefs.Count(definition => definition.MethodPointer == pointer) != 1 ||
            metadata.methodDefs.Any(definition => definition.MethodPointer > pointer && definition.MethodPointer < end) ||
            !metadata.methodDefs.Any(definition => ReferenceEquals(definition, method.Definition)) ||
            X64GenericMethodTableProof.TryIdentify(app, pe, index) is not { } generic ||
            generic.MethodPointers.ToArray().Any(value => value >= pointer && value < end) ||
            generic.AdjustorThunks.ToArray().Any(value => value >= pointer && value < end) ||
            generic.Invokers.ToArray().Any(value => value >= pointer && value < end)) return false;
        bindings.Add(generic.Origin);
        bindings.Add(Digest(System.Runtime.InteropServices.MemoryMarshal.AsBytes(generic.MethodPointers)));
        bindings.Add(Digest(System.Runtime.InteropServices.MemoryMarshal.AsBytes(generic.AdjustorThunks)));
        bindings.Add(Digest(System.Runtime.InteropServices.MemoryMarshal.AsBytes(generic.Invokers)));
        var matches = 0;
        foreach (var original in metadata.AssemblyDefinitions)
        {
            var assembly = app.ResolveContextForAssembly(original);
            if (!X64OriginalReferenceClassProof.CanonicalAssembly(assembly) || assembly.CodeGenModule is not { } module ||
                module.methodPointerCount is < 0 or > 1_000_000) return false;
            var cached = app.Binary.GetCodegenModuleMethodPointers(app.Binary.GetCodegenModuleIndex(module));
            if (cached.Length != module.methodPointerCount) return false;
            if (cached.Length == 0) continue;
            var size = checked((uint)cached.Length * sizeof(ulong));
            var offset = MapMethodPointerTable(pe, index, module.methodPointers, size);
            if (offset < 0 || size > pe.GetRawBinaryContent().Length - (long)offset) return false;
            var bytes = pe.GetRawBinaryContent().Slice(offset, checked((int)size));
            for (var ordinal = 0; ordinal < cached.Length; ordinal++)
            {
                var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(ordinal * sizeof(ulong), sizeof(ulong)));
                if (value != cached[ordinal]) return false;
                if (value == pointer) matches++;
                else if (value > pointer && value < end) return false;
            }
            bindings.Add(module); bindings.Add(module.methodPointerCount); bindings.Add(module.methodPointers);
            bindings.Add(Digest(bytes));
        }
        return matches == 1 && app.MethodsByAddress.TryGetValue(pointer, out var aliases) &&
            aliases is [var alias] && ReferenceEquals(alias, method);
    }

    private static int MapMethodPointerTable(PE pe, X64UnwindProof.Index index, ulong address, uint size)
    {
        if (size == 0 || (address & (sizeof(ulong) - 1)) != 0) return -1;
        var readOnly = index.MapReadOnlyData(address, size);
        if (readOnly >= 0) return readOnly;
        // Runtime registration tables can occupy writable data. Qualifying their
        // original file bytes does not establish runtime immutability: every slot
        // is compared with the parsed table and retained in the saved digest.
        if (address < index.ImageBase || address - index.ImageBase > uint.MaxValue ||
            !index.IsWritableVirtualRangeInOneSection((uint)(address - index.ImageBase), size) ||
            !X64MetadataStaticGetterProof.FileBackedWritableData(pe, index, address, size)) return -1;
        return checked((int)pe.MapVirtualAddressToRaw(address, false));
    }

    private static string Digest(ReadOnlySpan<byte> bytes)
    {
        using var hash = SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(bytes.ToArray()));
    }
}
