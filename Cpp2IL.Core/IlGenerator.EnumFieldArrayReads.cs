using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    internal static void ValidateEnumFieldArrayReads(MethodAnalysisContext method)
    {
        if (X86FieldArrayAccessProof.GetEnumReadEvidence(method) is not { } recorded)
            return;

        var current = X86FieldArrayAccessProof.Find(method, X86Utils.Iterate(method).ToArray());
        var instructions = method.ControlFlowGraph is { } graph ? LinearInstructions(graph) : null;
        if (current != recorded || instructions is not
            [
                { OpCode: OpCode.Move, IntegerBitWidth: 0, CallSemantics: CallSemantics.Direct,
                    NativeAddress: var fieldAddress,
                    Operands: [LocalVariable array, FieldReference field] },
                { OpCode: OpCode.Move, IntegerBitWidth: 32, CallSemantics: CallSemantics.Direct,
                    NativeAddress: var elementAddress,
                    Operands: [LocalVariable result, ArrayAccess access] } read,
                { OpCode: OpCode.Return, CallSemantics: CallSemantics.Direct,
                    Operands: [LocalVariable returned] }
            ] || fieldAddress != recorded.FieldReadAddress ||
            elementAddress != recorded.ElementReadAddress ||
            !ReferenceEquals(field.Field, recorded.Field) ||
            field.Offset != recorded.Field.Offset ||
            !ReferenceEquals(access.Array, array) ||
            array.Type is not SzArrayTypeAnalysisContext { ElementType: var element } ||
            !ReferenceEquals(element, method.ReturnType) ||
            !ReferenceEquals(result.Type, element) ||
            !ReferenceEquals(returned, result))
            throw EnumArrayFailure("the native proof, typed field/read/return or operation order changed");

        if (recorded.Shape.Kind is X86FieldArrayAccessProof.AccessKind.ReadFirst or
            X86FieldArrayAccessProof.AccessKind.ReadFixed)
        {
            if (access.Index is not Immediate { Value: var index } || index != recorded.Shape.FixedIndex)
                throw EnumArrayFailure("the proved fixed index changed");
        }
        var memory = X86FieldArrayAccessProof.ElementMemory(recorded.Shape, array, access.Index);
        if (!X86FieldArrayAccessProof.IsProvedEnumRead(method, read, 1, memory, array, element))
            throw EnumArrayFailure("the read lost its proved receiver, enum storage or index");
    }

    private static DecompilerException EnumArrayFailure(string detail) =>
        new("Enum field-array proof no longer matches final managed operations: " + detail);
}
