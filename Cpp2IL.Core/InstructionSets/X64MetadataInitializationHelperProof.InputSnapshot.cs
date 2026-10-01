using System;
using System.Collections.Generic;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64MetadataInitializationHelperProof
{
    /// <summary>
    /// Retains the bytes consumed by the existing TypeInfo contract. This adds
    /// freshness evidence; it does not qualify new helpers or lower callbacks.
    /// </summary>
    internal static bool TryCaptureTypeInfoInput(ApplicationAnalysisContext app, PE pe,
        X64UnwindProof.Index unwind, ulong target, out string snapshot)
    {
        snapshot = "";
        try
        {
            if (!TryIdentifyTypeInfo(app, pe, unwind, target) ||
                Read(pe, unwind, target, 1, 5) is not { } thunk ||
                Read(pe, unwind, thunk[0].NearBranchTarget, 2, 7) is not { } wrapper) return false;
            var parts = new List<string>();
            if (!Bytes(target, 16) || !Bytes(wrapper[0].IP, 16)) return false;
            var core = wrapper[1].NearBranchTarget;
            var primary = unwind.ClassifySpan(core, core + 1).End - core == 0x5d;
            ulong resolver;
            uint table;
            if (primary)
            {
                if (Read(pe, unwind, core + 0x5d, 8, 0x23) is not { } dispatch ||
                    !TableLoad(dispatch[1], out table) ||
                    !Bytes(core, 0x80) || !Bytes(dispatch[7].NearBranchTarget, 28)) return false;
                resolver = dispatch[5].NearBranchTarget;
            }
            else
            {
                if (Read(pe, unwind, core + 0x37, 15, 0x3a) is not { } dispatch ||
                    !TableLoad(dispatch[12], out table) ||
                    Read(pe, unwind, core + 0x71, 5, 0x13) is not { } arm ||
                    !TryReadMethodDefSwitchArm(pe, unwind, table, out var methodArm) ||
                    Read(pe, unwind, methodArm, 3, 0x0d) is not { } methods ||
                    Read(pe, unwind, methods[0].NearBranchTarget, 29, 0x58) is not { } encoded ||
                    Read(pe, unwind, encoded[18].NearBranchTarget, 35, 0x8b) is not { } definition ||
                    !Bytes(core, 0x84) || !Bytes(arm[4].NearBranchTarget, 0x21) ||
                    !Bytes(methodArm, 0x0d) || !Bytes(encoded[0].IP, 0x58) || !Bytes(definition[0].IP, 0x8b) ||
                    !Bytes(definition[13].NearBranchTarget, 20) || !Bytes(definition[16].NearBranchTarget, 18)) return false;
                resolver = arm[2].NearBranchTarget;
            }
            if (!Bytes(unwind.ImageBase + table, 28) ||
                Read(pe, unwind, resolver, primary ? 33 : 38, primary ? 0x7c : 0x8d) is not { } type ||
                !Bytes(resolver, primary ? 0x7c : 0x8d)) return false;
            var init = type[primary ? 24 : 29].NearBranchTarget;
            if (Read(pe, unwind, init, 16, 0x31) is not { } slow || !Bytes(init, 0x31)) return false;
            var throwing = slow[7].NearBranchTarget;
            var directSpan = unwind.ClassifySpan(throwing, throwing + 1);
            var length = checked((int)(directSpan.End - throwing));
            if (!Bytes(throwing, length)) return false;
            foreach (var name in new[] { "il2cpp_class_from_type", "il2cpp_class_from_il2cpp_type",
                         "il2cpp_gchandle_get_target", "il2cpp_raise_exception" })
            {
                var exported = X64PeExportProof.Find(pe, unwind, name);
                if (exported == 0 || !Bytes(exported, 16)) return false;
            }
            var objectNew = X64PeExportProof.Find(pe, unwind, "il2cpp_object_new");
            if (Read(pe, unwind, objectNew, 6, 18, allowChainedRegion: true) is not { } api ||
                unwind.GetHandler(objectNew) is not { } handler ||
                !Bytes(objectNew, checked((int)(handler.End - objectNew))) ||
                !Bytes(api[1].NearBranchTarget, 18)) return false;
            snapshot = string.Join(";", parts);
            return true;

            bool Bytes(ulong address, int count)
            {
                if (count <= 0 || count > 4096 || address > ulong.MaxValue - (ulong)count) return false;
                var first = pe.MapVirtualAddressToRaw(address, false);
                var last = pe.MapVirtualAddressToRaw(address + (ulong)count - 1, false);
                var image = pe.GetRawBinaryContent();
                if (first < 0 || first > image.Length - count || last != first + count - 1) return false;
                parts.Add(address.ToString("X16") + ":" + Convert.ToBase64String(image.Slice(checked((int)first), count).ToArray()));
                return true;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }
}
