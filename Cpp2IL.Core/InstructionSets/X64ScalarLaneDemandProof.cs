using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

// Qualifies lane transport, not floating arithmetic or caller initialization.
// The existing scalar arithmetic lifter and its typed emission still own those
// operations. Packed memory, lane observers and environment changes fail closed.
internal static partial class X64ScalarLaneDemandProof
{
    internal const string EvidenceKey = "X64ScalarLaneDemandProof";
    private const int MaximumBytes = 512;
    private static readonly X64CallingConventionResolver CallingConventions = new();
    internal readonly record struct LiteralRead(ulong Address, int Width, string Bytes);
    internal readonly record struct DescriptorBinding(Il2CppType Type, ulong Address, ulong Datapoint,
        uint Bits, ulong Data, uint Attributes, Il2CppTypeEnum Tag, uint Modifiers, uint Byref,
        uint Pinned, uint ValueType, string Bytes);

    internal sealed class Evidence
    {
        private readonly Instruction[] _body;
        private readonly MethodAnalysisContext[] _aliases;
        private readonly LiteralRead[] _literals;
        private readonly string _signature;
        private readonly string _closedBytes;
        private readonly KeyValuePair<Register, int>[] _incoming;
        private readonly DescriptorBinding[] _descriptors;
        internal MethodAnalysisContext Method { get; }
        internal Shape Shape { get; }
        internal int ReturnWidth { get; }
        internal ReadOnlySpan<Instruction> Body => _body;
        internal ReadOnlySpan<LiteralRead> Literals => _literals;
        internal ReadOnlySpan<KeyValuePair<Register, int>> Incoming => _incoming;

        internal Evidence(MethodAnalysisContext method, Shape shape, Instruction[] body,
            MethodAnalysisContext[] aliases, LiteralRead[] literals, string signature, string closedBytes,
            IReadOnlyDictionary<Register, int> incoming, int returnWidth, DescriptorBinding[] descriptors)
        {
            Method = method;
            Shape = shape;
            _body = body.ToArray();
            _aliases = aliases.ToArray();
            _literals = literals.ToArray();
            _signature = signature;
            _closedBytes = closedBytes;
            _incoming = incoming.OrderBy(pair => pair.Key).ToArray();
            ReturnWidth = returnWidth;
            _descriptors = descriptors.ToArray();
        }

        internal bool IsUnchanged() => Find(Method) is { } current &&
            _body.SequenceEqual(current._body) && _aliases.SequenceEqual(current._aliases) &&
            _literals.SequenceEqual(current._literals) && _signature == current._signature &&
            _closedBytes == current._closedBytes && ReturnWidth == current.ReturnWidth &&
            _incoming.SequenceEqual(current._incoming) && _descriptors.SequenceEqual(current._descriptors) &&
            Shape.Projections.SequenceEqual(current.Shape.Projections);
    }

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey) || method.GetExtraData<Evidence>(EvidenceKey) != null;

    internal static bool CanProject(MethodAnalysisContext? method, Instruction site, out int width)
    {
        width = 0;
        if (method == null || !(IsPackedCopy(site) || IsSelfZero(site)) || Find(method) is not { } proof)
            return false;
        foreach (var projection in proof.Shape.Projections)
        {
            if (projection.Address != site.IP || !proof.Body.ToArray().Contains(site)) continue;
            var saved = method.GetExtraData<Evidence>(EvidenceKey);
            if (HasEvidence(method) && (saved == null || !saved.IsUnchanged())) return false;
            method.PutExtraData(EvidenceKey, proof);
            NativeRecoveryProofTracker.Mark(method, EvidenceKey);
            width = projection.Width;
            return true;
        }
        return false;
    }

    internal static bool IsValidFor(MethodAnalysisContext method) =>
        !HasEvidence(method) || method.GetExtraData<Evidence>(EvidenceKey) is { } proof && proof.IsUnchanged();

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            var descriptors = new List<DescriptorBinding>();
            if (!TryBindSignature(method, descriptors, out var incoming, out var returnWidth, out var signature) ||
                app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } index ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
                bindings.Count == 0 || bindings.Distinct().Count() != bindings.Count ||
                !bindings.Any(alias => ReferenceEquals(alias, method))) return null;
            var aliases = bindings.OrderBy(alias => alias.Definition?.MethodIndex.Value).ToArray();
            foreach (var alias in aliases)
            {
                if (!ReferenceEquals(alias.AppContext, app) || alias.UnderlyingPointer != method.UnderlyingPointer ||
                    !TryBindSignature(alias, descriptors, out var aliasIncoming, out var aliasReturn, out var aliasSignature) ||
                    aliasReturn != returnWidth || aliasIncoming.Count != incoming.Count ||
                    incoming.Any(pair => !aliasIncoming.TryGetValue(pair.Key, out var width) || width != pair.Value))
                    return null;
                signature += "|alias:" + aliasSignature;
            }

            if (ReadCompleteBody(method, pe, index, out var closedBytes) is not { } body ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null) return null;
            var literals = new List<LiteralRead>();
            if (TryProveShape(body, incoming, returnWidth, (site, width) =>
                    CaptureLiteral(pe, index, site, width, literals)) is not { } shape) return null;
            return new(method, shape, body, aliases, literals.OrderBy(read => read.Address).ToArray(), signature, closedBytes,
                incoming, returnWidth, descriptors.ToArray());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException or
                                          KeyNotFoundException or NullReferenceException)
        {
            return null;
        }
    }

    private static bool TryBindSignature(MethodAnalysisContext method, List<DescriptorBinding> descriptors,
        out Dictionary<Register, int> incoming, out int returnWidth, out string binding)
    {
        incoming = new();
        returnWidth = 0;
        binding = "";
        var app = method.AppContext;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.MetadataVersion != 29 ||
            !app.Binary.HasOriginalGenericRegistrationContext(app.LibCpp2IlContext) ||
            method is ConcreteGenericMethodAnalysisContext || !method.IsStatic || method.IsVirtual ||
            method.Name is ".ctor" or ".cctor" || method.GenericParameters.Count != 0 ||
            method.Definition is not { GenericContainer: null, IsUnmanagedCallersOnly: false } definition ||
            method.DeclaringType is not { Definition: { GenericContainer: null } ownerDefinition } owner ||
            !ReferenceEquals(owner.AppContext, app) || owner.GenericParameters.Count != 0 ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes || owner.OverrideBaseType != null ||
            !ReferenceEquals(definition.DeclaringType, ownerDefinition) ||
            !ReferenceEquals(app.ResolveContextForMethod(definition), method) ||
            !X64OriginalReferenceClassProof.OriginalType(app, ownerDefinition) ||
            !X64OriginalReferenceClassProof.OriginalMethod(app, definition) ||
            !X64OriginalReferenceClassProof.OriginalMethodPointer(method) ||
            method.OverrideName != null || method.OverrideAttributes != null || method.OverrideImplAttributes != null ||
            method.OverrideReturnType != null || !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) != 0 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) || CallingConventions.ReturnsViaHiddenBuffer(method) ||
            CallingConventions.ReturnRegister(method).Name != "xmm0" || method.Parameters.Count > 4 ||
            (returnWidth = ScalarTypeWidth(method.ReturnType, definition.RawReturnType)) == 0)
            return false;

        var arguments = CallingConventions.ResolveForManaged(method);
        if (arguments.Length != method.Parameters.Count + 1 ||
            !CaptureDescriptor(app, definition.RawReturnType!, descriptors)) return false;
        var parts = new List<string>
        {
            definition.MethodIndex.Value.ToString(), definition.token.ToString(),
            definition.returnTypeIdx.Value.ToString(), definition.flags.ToString(), definition.iflags.ToString(),
            ownerDefinition.TypeIndex.Value.ToString(), owner.Name, owner.Namespace, method.Name, returnWidth.ToString()
        };
        for (var ordinal = 0; ordinal < method.Parameters.Count; ordinal++)
        {
            var parameter = method.Parameters[ordinal];
            if (parameter.Definition is not { } original || parameter.ParameterIndex != ordinal ||
                !ReferenceEquals(parameter.DeclaringMethod, method) || parameter.IsRef ||
                parameter.OverrideParameterType != null || parameter.UseOverrideDefaultValue ||
                parameter.Name != parameter.DefaultName || parameter.Attributes != parameter.DefaultAttributes ||
                !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) ||
                !X64OriginalReferenceClassProof.OriginalParameter(app, definition, ordinal, original) ||
                ScalarTypeWidth(parameter.ParameterType, original.RawType) is not (32 or 64) ||
                arguments[ordinal] is not Cpp2IL.Core.ISIL.Register register ||
                !Enum.TryParse<Register>(register.Name, true, out var nativeRegister) ||
                nativeRegister is < Register.XMM0 or > Register.XMM3)
                return false;
            if (!CaptureDescriptor(app, original.RawType!, descriptors)) return false;
            var width = ScalarTypeWidth(parameter.ParameterType, original.RawType);
            if (incoming.ContainsKey(nativeRegister)) return false;
            incoming.Add(nativeRegister, width);
            parts.Add(original.token + ":" + original.typeIndex.Value + ":" + parameter.Name + ":" +
                (ushort)parameter.Attributes + ":" + width);
        }
        binding = string.Join("|", parts);
        return true;
    }

    private static bool CaptureDescriptor(ApplicationAnalysisContext app, Il2CppType raw,
        List<DescriptorBinding> descriptors)
    {
        if (!app.Binary.TryGetTypeVirtualAddress(raw, out var address) || address > ulong.MaxValue - 11)
            return false;
        var offset = app.Binary.MapVirtualAddressToRaw(address, false);
        var image = app.Binary.GetRawBinaryContent();
        if (offset < 0 || offset > image.Length - 12 ||
            app.Binary.MapVirtualAddressToRaw(address + 11, false) != offset + 11) return false;
        descriptors.Add(new(raw, address, raw.Datapoint, raw.Bits, raw.Data.Dummy, raw.Attrs,
            raw.Type, raw.NumMods, raw.Byref, raw.Pinned, raw.ValueType,
            Convert.ToBase64String(image.Slice(checked((int)offset), 12).ToArray())));
        return true;
    }

    private static int ScalarTypeWidth(TypeAnalysisContext type, Il2CppType? raw)
    {
        if (raw is not { NumMods: 0, Byref: 0, Pinned: 0 } ||
            !X64OriginalReferenceClassProof.RetainedDescriptor(type.AppContext, raw)) return 0;
        if (ReferenceEquals(type, type.AppContext.SystemTypes.SystemSingleType) && raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_R4)
            return 32;
        return ReferenceEquals(type, type.AppContext.SystemTypes.SystemDoubleType) && raw.Type == Il2CppTypeEnum.IL2CPP_TYPE_R8 ? 64 : 0;
    }

    private static Instruction[]? ReadCompleteBody(MethodAnalysisContext method, PE pe,
        X64UnwindProof.Index index, out string closedBytes)
    {
        closedBytes = "";
        if (method.RawBytes.Length == 0) method.EnsureRawBytes();
        var body = X64NativeInstructionReader.ReadRootBody(method);
        if (body != null)
        {
            var count = body.Length;
            while (count > 0 && body[count - 1].Code == Code.Int3) count--;
            if (count == 0 || body[^1].NextIP - method.UnderlyingPointer > MaximumBytes) return null;
            closedBytes = Capture(body[^1].NextIP);
            return body.Take(count).ToArray();
        }
        var prefix = X86Utils.Iterate(method).Take(64).TakeWhile(instruction => instruction.Code != Code.Int3).ToArray();
        var returned = Array.FindIndex(prefix, instruction => instruction.Code == Code.Retnq);
        if (returned < 0 || X64NativeInstructionReader.ReadFramelessLeaf(method, returned + 1, MaximumBytes) is not { } leaf)
            return null;
        var end = leaf[^1].NextIP;
        if (end > ulong.MaxValue - 15) return null;
        var paddedEnd = (end + 15) & ~15UL;
        if (index.ClassifySpan(method.UnderlyingPointer, paddedEnd).Kind != X64UnwindProof.SpanKind.NoEntry ||
            X64NativeInstructionReader.HasInteriorManagedEntry(method.AppContext, method.UnderlyingPointer, paddedEnd) ||
            !X64NativePaddingProof.HasInt3Padding(pe, end, paddedEnd) ||
            !index.IsUnaffectedByBaseRelocation(method.UnderlyingPointer, checked((uint)(paddedEnd - method.UnderlyingPointer))))
            return null;
        closedBytes = Capture(paddedEnd);
        if (!X64AncestorConstructorThunkProof.FileBackedExecutable(pe, index,
                Convert.FromBase64String(closedBytes), method.UnderlyingPointer)) return null;
        return leaf;

        string Capture(ulong end)
        {
            var offset = pe.MapVirtualAddressToRaw(method.UnderlyingPointer, false);
            var length = checked((int)(end - method.UnderlyingPointer));
            return Convert.ToBase64String(pe.GetRawBinaryContent().Slice(checked((int)offset), length).ToArray());
        }
    }

    private static bool CaptureLiteral(PE pe, X64UnwindProof.Index index, Instruction instruction,
        int width, List<LiteralRead> captured)
    {
        if (!instruction.IsIPRelativeMemoryOperand || instruction.MemoryIndex != Register.None ||
            width is not (32 or 64)) return false;
        var address = instruction.IPRelativeMemoryAddress;
        var length = checked((uint)(width / 8));
        var offset = index.MapReadOnlyData(address, length);
        if (offset < 0 || !index.IsUnaffectedByBaseRelocation(address, length)) return false;
        var image = pe.GetRawBinaryContent();
        if (offset > image.Length - (long)length) return false;
        var bytes = image.Slice(checked((int)offset), checked((int)length));
        var bits = width == 32 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        var exponent = width == 32 ? bits & 0x7F800000u : bits & 0x7FF0000000000000UL;
        var mantissa = width == 32 ? bits & 0x007FFFFFu : bits & 0x000FFFFFFFFFFFFFUL;
        // Keep exceptional literal representations outside this transport
        // proof until their emission and floating-environment contracts exist.
        if (mantissa != 0 && (exponent == 0 || exponent == (width == 32 ? 0x7F800000u : 0x7FF0000000000000UL)))
            return false;
        captured.Add(new(address, width, Convert.ToBase64String(bytes.ToArray())));
        return true;
    }
}
