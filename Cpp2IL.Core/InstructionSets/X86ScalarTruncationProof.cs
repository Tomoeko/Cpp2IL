using System.Collections.Generic;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;
using IsilInstruction = Cpp2IL.Core.ISIL.Instruction;
using IsilRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Proves a complete binary64 parameter-to-signed-return conversion on Windows x64.</summary>
internal static class X86ScalarTruncationProof
{
    public static List<IsilInstruction>? TryLift(MethodAnalysisContext context, IReadOnlyList<Instruction> body)
    {
        var owner = context.DeclaringType;
        var types = context.AppContext.SystemTypes;
        if (context.AppContext.Binary is not PE { PointerSizeBytes: 8 } ||
            context.AppContext.Binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
            context.AppContext.UnityVersion.ToString() != "2021.3.35f1" ||
            context.Definition is not { parameterCount: 1, GenericContainer: null } definition ||
            owner?.Definition is not { GenericContainer: null } || owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) || owner.Attributes != owner.DefaultAttributes ||
            context.GenericParameters.Count != 0 || !context.IsStatic || context.Attributes != context.DefaultAttributes ||
            context.ImplAttributes != context.DefaultImplAttributes ||
            (context.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (context.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
            context.Parameters is not [var parameter] || definition.InternalParameterData is not [var originalParameter] ||
            parameter.Definition == null || parameter.Definition != originalParameter || parameter.ParameterIndex != 0 ||
            !ReferenceEquals(parameter.DeclaringMethod, context) || parameter.IsRef ||
            parameter.Attributes != parameter.DefaultAttributes ||
            !ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) ||
            !ReferenceEquals(parameter.ParameterType, types.SystemDoubleType) ||
            !ReferenceEquals(context.ReturnType, context.DefaultReturnType) ||
            parameter.Definition.RawType is not { NumMods: 0, Byref: 0, Pinned: 0 } ||
            definition.RawReturnType is not { NumMods: 0, Byref: 0, Pinned: 0 } ||
            body.Count == 0 || body[0].IP != context.UnderlyingPointer)
            return null;

        var returnBits = ReferenceEquals(context.ReturnType, types.SystemInt32Type) ? 32 :
            ReferenceEquals(context.ReturnType, types.SystemInt64Type) ? 64 : 0;
        return TryLift(body, returnBits);
    }

    // The caller of this overload establishes a canonical binary64 argument in XMM0.
    internal static List<IsilInstruction>? TryLift(IReadOnlyList<Instruction> body, int returnBits)
    {
        if (body.Count < 2 || returnBits is not (32 or 64))
            return null;
        var conversion = body[0];
        var returned = body[1];
        if (!HasPlainEncoding(conversion) || !HasPlainEncoding(returned) ||
            conversion.Code != (returnBits == 32 ? Code.Cvttsd2si_r32_xmmm64 : Code.Cvttsd2si_r64_xmmm64) ||
            conversion.Length != (returnBits == 32 ? 4 : 5) ||
            conversion.OpCount != 2 || conversion.Op0Kind != OpKind.Register || conversion.Op1Kind != OpKind.Register ||
            conversion.Op0Register != (returnBits == 32 ? Register.EAX : Register.RAX) ||
            conversion.Op1Register != Register.XMM0 || conversion.FlowControl != FlowControl.Next ||
            returned.IP != conversion.NextIP || returned.Code != Code.Retnq || returned.OpCount != 0 || returned.Length != 1)
            return null;

        // There is no outgoing edge into any decoded suffix. The narrow return observes EAX,
        // not the entire RAX register; do not reuse this result as a general register write.
        var result = new IsilRegister(null, "scalar_truncation_result");
        return
        [
            new(0, ISIL.OpCode.FloatTruncateSigned, result, new IsilRegister(null, "xmm0"),
                new ISIL.Immediate(64), new ISIL.Immediate(returnBits)),
            new(1, ISIL.OpCode.Return, result),
        ];
    }

    private static bool HasPlainEncoding(Instruction instruction) =>
        !instruction.IsInvalid && instruction.CodeSize == CodeSize.Code64 &&
        !instruction.HasLockPrefix && !instruction.HasRepPrefix && !instruction.HasRepnePrefix &&
        instruction.SegmentPrefix == Register.None;
    // Iced consumes the mandatory F2 opcode prefix; it is not a remaining REPNE prefix.
    // Exact instruction lengths additionally exclude redundant operand/address/REX prefixes.
}
