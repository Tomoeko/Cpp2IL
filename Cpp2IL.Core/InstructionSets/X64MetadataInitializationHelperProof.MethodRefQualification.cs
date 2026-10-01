using System;
using System.Buffers.Binary;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64MetadataInitializationHelperProof
{
    internal const string UnsupportedMethodRefReason =
        "MethodRef runtime initialization is unsupported: generic-cache, instantiation and native cleanup semantics are not fully qualified.";

    internal enum MethodRefDisposition
    {
        NotApplicable,
        Unsupported,
        Qualified
    }

    internal sealed record MethodRefObligation(MethodRefDisposition Disposition,
        ulong Initializer = 0, ulong Slot = 0, MetadataUsage? Usage = null,
        string? Reason = null, ulong RawEncoded = 0);

    // MethodDef and MethodRef share an upper switch destination, but their lower
    // resolvers have different semantics. A tag-3 proof cannot qualify tag 6.
    // The actual slot/specification scopes future qualification to the selected
    // instantiation; an initializer address alone cannot establish that proof.
    internal static bool TryIdentifyMethodRefArm(ApplicationAnalysisContext app,
        PE pe, X64UnwindProof.Index unwind, ulong target, MetadataUsage usage,
        ulong slot, out string reason)
    {
        reason = UnsupportedMethodRefReason;
        return false;
    }

    // This is a conservative output obligation, not an admission proof. Keep
    // recognition independent of helper success so an altered lower resolver
    // cannot evade the unsupported disposition by falling through other routes.
    internal static MethodRefObligation CheckGuardedMethodRef(MethodAnalysisContext method)
    {
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext))
            return new(MethodRefDisposition.NotApplicable);
        MethodRefObligation? recognized = null;
        try
        {
            method.EnsureRawBytes();
            if (X64NativeInstructionReader.ReadRootBody(method) is not { } body)
                return new(MethodRefDisposition.NotApplicable);

            ulong slot;
            ulong initializer;
            if (X64GenericInterfaceWrapperProof.TryProveShape(body, out var wrapper))
            {
                slot = wrapper.MethodSlot;
                initializer = wrapper.Initializer;
            }
            else if (X64GenericBaseConstructorProof.TryProveShape(body, out var constructor))
            {
                slot = constructor.MethodSlot;
                initializer = constructor.Initializer;
            }
            else
                return new(MethodRefDisposition.NotApplicable);

            if (method.AppContext.Binary is not PE pe)
                return new(MethodRefDisposition.NotApplicable);
            var first = pe.MapVirtualAddressToRaw(slot, false);
            var last = pe.MapVirtualAddressToRaw(checked(slot + 7), false);
            var image = pe.GetRawBinaryContent();
            if (first < 0 || first > image.Length - 8 || last - first != 7)
                return new(MethodRefDisposition.NotApplicable);
            var encoded = BinaryPrimitives.ReadUInt64LittleEndian(image.Slice(checked((int)first), 8));
            if ((encoded & 0xE000_0000) >> 29 != (ulong)MetadataUsageType.MethodRef)
                return new(MethodRefDisposition.NotApplicable);

            // Decode the original tag even when its index is invalid. The normal
            // metadata lookup intentionally hides out-of-range usages; that must
            // not hide an unresolved runtime initialization obligation.
            recognized = new(MethodRefDisposition.Unsupported, initializer, slot,
                Reason: UnsupportedMethodRefReason, RawEncoded: encoded);
            var usage = MetadataUsage.DecodeMetadataUsage(encoded, slot,
                method.AppContext.LibCpp2IlContext);
            return recognized with { Usage = usage };
        }
        catch (Exception exception) when (exception is ArgumentException or
                                          InvalidOperationException or IndexOutOfRangeException or
                                          OverflowException or NullReferenceException)
        {
            return recognized ?? new(MethodRefDisposition.NotApplicable);
        }
    }
}
