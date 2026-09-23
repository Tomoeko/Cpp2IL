using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using NullCheckedCall = Cpp2IL.Core.ISIL.NullCheckedCall;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// The complete file-backed leaf body of a one-parameter managed reference
/// null predicate. No other native flag consumer or side effect is present.
/// This also permits safe recovery when identical bodies share an address.
/// </summary>
internal static class X86ReferenceNullReturnProof
{
    internal static bool IsApplicable(MethodAnalysisContext method,
        IReadOnlyList<Instruction> native)
    {
        var app = method.AppContext;
        var start = method.UnderlyingPointer;
        if (native.Count != 3 || method.RawBytes.Length != 7 ||
            !method.IsStatic || method.IsVirtual || method.Parameters.Count != 1 ||
            method.Name != method.DefaultName ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            method.GenericParameters.Count != 0 ||
            method.OverrideReturnType != null ||
            !ReferenceEquals(method.ReturnType, app.SystemTypes.SystemBooleanType) ||
            method.Definition?.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN,
                NumMods: 0, Byref: 0, Pinned: 0 } ||
            method.Parameters[0] is not { ParameterIndex: 0, IsRef: false } parameter ||
            !ReferenceEquals(parameter.DeclaringMethod, method) ||
            parameter.Attributes != parameter.DefaultAttributes ||
            parameter.OverrideParameterType != null ||
            parameter.Definition?.RawType is not { NumMods: 0, Byref: 0, Pinned: 0,
                Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_OBJECT or
                    Il2CppTypeEnum.IL2CPP_TYPE_STRING or Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY } ||
            !NullCheckedCall.SameOrdinaryType(parameter.ParameterType,
                parameter.DefaultParameterType) ||
            !(NullCheckedCall.IsReferenceClass(parameter.ParameterType) ||
              NullCheckedCall.IsBoundedArrayReference(parameter.ParameterType)) ||
            !X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            !MatchesShape(native, start, method.RawBytes.Length) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                requireUniqueBinding: false) ||
            Enumerable.Range(1, 6).Any(offset =>
                app.MethodsByAddress.ContainsKey(start + (ulong)offset)))
            return false;

        var end = native[^1].NextIP;
        return X64UnwindProof.ForApplication(app)?.ClassifySpan(start, end)
            is { Kind: X64UnwindProof.SpanKind.NoEntry, Start: var begin, End: var spanEnd } &&
            begin == start && spanEnd == end;
    }

    internal static bool MatchesShape(IReadOnlyList<Instruction> native, ulong start,
        int bodyBytes)
    {
        if (bodyBytes != 7 || native is not [{ } test, { } set, { } ret] ||
            test.IP != start || set.IP != test.NextIP || ret.IP != set.NextIP ||
            ret.NextIP != start + (ulong)bodyBytes ||
            native.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 ||
                instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix ||
                instruction.SegmentPrefix != Register.None) ||
            test.Mnemonic != Mnemonic.Test || test.OpCount != 2 ||
            test.Op0Kind != OpKind.Register || test.Op1Kind != OpKind.Register ||
            test.Op0Register != Register.RCX || test.Op1Register != Register.RCX ||
            set.Mnemonic is not (Mnemonic.Sete or Mnemonic.Setne) ||
            set.OpCount != 1 || set.Op0Kind != OpKind.Register ||
            set.Op0Register != Register.AL ||
            ret.Code != Code.Retnq || ret.OpCount != 0)
            return false;
        return true;
    }
}
