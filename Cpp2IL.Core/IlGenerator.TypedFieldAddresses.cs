using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private static void ValidateTypedFieldAddresses(MethodAnalysisContext method)
    {
        var hasAddresses = method.ControlFlowGraph!.Instructions.Any(instruction =>
            instruction.Operands.Any(operand => operand is AddressOf { Target: FieldReference }));
        if ((hasAddresses || TypedFieldAddressRecovery.HasEvidence(method)) &&
            !TypedFieldAddressRecovery.IsValidFor(method))
            throw new DecompilerException("Typed field addresses no longer match their native storage, call and first-fault evidence");
    }

    private static void LoadTypedFieldAddress(FieldReference field, MethodDefinition method, EmissionLocals locals)
    {
        if (!TypedFieldAddressRecovery.IsAdmittedAddress(locals.Context, field))
            throw new DecompilerException("Managed field address has no authenticated native storage and call evidence");
        LoadLocal(field.Local, method, locals);
        method.CilMethodBody!.Instructions.Add(CilOpCodes.Ldflda, field.Field.ToFieldDescriptor());
    }
}
