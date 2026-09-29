using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateParameterBooleanArrayStores(MethodAnalysisContext method)
    {
        if (X64ParameterBooleanArrayStoreProof.GetEvidence(method) is not { } recorded)
            return;
        var current = X64ParameterBooleanArrayStoreProof.Find(method,
            X86Utils.Iterate(method).ToArray());
        var graph = method.ControlFlowGraph;
        var instructions = graph == null ? null : LinearInstructions(graph);
        if (current != recorded || graph == null || graph.EntryBlock.Instructions.Count != 0 ||
            graph.ExitBlock.Instructions.Count != 0 || instructions is not
            [
                { OpCode: OpCode.Move, IntegerBitWidth: 0,
                    CallSemantics: CallSemantics.Direct, NativeAddress: var storeAddress,
                    Operands: [ArrayAccess access, var value] },
                { OpCode: OpCode.Return, IntegerBitWidth: 0,
                    CallSemantics: CallSemantics.Direct, NativeAddress: var returnAddress,
                    Operands.Count: 0 }
            ] || storeAddress != recorded.StoreAddress || returnAddress != recorded.ReturnAddress ||
            !StoreParameter(method, access.Array, recorded.ArrayParameter,
                recorded.Shape.ArrayEntry) ||
            access.Array.Type is not SzArrayTypeAnalysisContext { ElementType: var element } ||
            !ReferenceEquals(element, method.AppContext.SystemTypes.SystemBooleanType) ||
            access.Index is not LocalVariable index ||
            !StoreParameter(method, index, recorded.IndexParameter, recorded.Shape.IndexEntry))
            throw ParameterBooleanStoreFailure("the native proof, typed array/index or sole store/return changed");
        if (recorded.Shape.Literal is { } literal)
        {
            if (value is not Immediate { Value: var immediate } || immediate != literal)
                throw ParameterBooleanStoreFailure("the canonical Boolean literal changed");
        }
        else if (recorded.ValueParameter is not { } parameter || value is not LocalVariable source ||
                 !StoreParameter(method, source, parameter, recorded.Shape.ValueEntry) ||
                 !ReferenceEquals(source.Type, method.AppContext.SystemTypes.SystemBooleanType))
            throw ParameterBooleanStoreFailure("the stored Boolean lost its incoming parameter identity");
    }

    private static bool StoreParameter(MethodAnalysisContext method, LocalVariable local,
        ParameterAnalysisContext parameter, Iced.Intel.Register entry) =>
        method.ParameterLocals.Contains(local) &&
        method.ParameterLocals.Count(candidate =>
            LocalVariables.GetIncomingParameterIndex(method, candidate) == parameter.ParameterIndex) == 1 &&
        LocalVariables.GetIncomingParameterIndex(method, local) == parameter.ParameterIndex &&
        local.Register.Name == X86Utils.GetRegisterName(entry) &&
        NullCheckedCall.SameOrdinaryType(local.Type, parameter.ParameterType);

    private static DecompilerException ParameterBooleanStoreFailure(string detail) =>
        new("Parameter Boolean-array store proof no longer matches final managed operations: " + detail);
}
