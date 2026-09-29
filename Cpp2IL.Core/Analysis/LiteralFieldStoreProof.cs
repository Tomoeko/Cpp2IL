using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Binds one native immediate store to unchanged Boolean, Int32 or UInt32 storage.
/// The guard coalescer preserves receiver failure and surrounding effects.
/// Player metadata and MOV encoding do not establish an authored volatile
/// modifier; declaration uncertainty remains separate from this operation proof.
/// </summary>
internal static class LiteralFieldStoreProof
{
    internal static bool IsValidFor(MethodAnalysisContext method, Instruction operation,
        FieldReference access, Immediate literal)
    {
        var app = method.AppContext;
        var graph = method.ControlFlowGraph;
        var receiver = access.Local;
        var field = access.Field;
        var width = StorageWidth(method, access, literal);
        if (width == 0 || !X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            graph == null || !graph.Instructions.Contains(operation) ||
            operation is not { OpCode: OpCode.Move, IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct, Operands: [FieldReference stored, Immediate value],
                NativeAddress: { } address } ||
            !ReferenceEquals(stored, access) || value.Value != literal.Value || receiver.Type == null ||
            field.Visibility != FieldAttributes.Public ||
            (field.Attributes & FieldAttributes.InitOnly) != 0 ||
            field.BackingData?.Field.RawFieldType is not { NumMods: 0, Byref: 0, Pinned: 0 } ||
            !NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, width) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            method.Name is ".ctor" or ".cctor" ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                requireUniqueBinding: false) || RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            OperandEffects.LocalsWithMutableStorage(graph.Instructions).Any(local =>
                local.Register.Number == receiver.Register.Number) ||
            !X64NativeLiteralFieldStoreProof.IsValidFor(method, pe, unwind,
                address, access, unchecked((uint)literal.Value), width,
                proveReceiverOrigin: true))
            return false;

        var definitions = graph.Instructions.Where(instruction =>
            ReferenceEquals(instruction.Destination, receiver)).ToArray();
        if (method.ParameterLocals.Contains(receiver))
        {
            if (definitions.Length != 0)
                return false;
            if (receiver.IsThis)
                return !method.IsStatic && ReferenceEquals(receiver.Type, method.DeclaringType);
            if (LocalVariables.GetIncomingParameterIndex(method, receiver) is not { } index)
                return false;
            var parameter = method.Parameters[index];
            return !parameter.IsRef && parameter.OverrideParameterType == null &&
                   ReferenceEquals(parameter.ParameterType, receiver.Type) &&
                   ReferenceEquals(parameter.DefaultParameterType, receiver.Type) &&
                   parameter.Attributes == parameter.DefaultAttributes;
        }

        if (definitions is not [var origin] ||
            graph.FindBlockByInstruction(origin) is not { } producerBlock ||
            graph.FindBlockByInstruction(operation) is not { } storeBlock ||
            !(CallResultNullGuardProof.HasBoundProducer(method, receiver, origin) ||
              FieldLoadReceiverProof.HasBoundProducer(method, receiver, origin, operation)))
            return false;
        return ReferenceEquals(producerBlock, storeBlock)
            ? producerBlock.Instructions.IndexOf(origin) < storeBlock.Instructions.IndexOf(operation)
            : new DominatorInfo(graph).Dominates(producerBlock, storeBlock);
    }

    private static int StorageWidth(MethodAnalysisContext method, FieldReference access, Immediate literal)
    {
        var types = method.AppContext.SystemTypes;
        var field = access.Field;
        var raw = field.BackingData?.Field.RawFieldType;
        if (ReferenceEquals(field.FieldType, types.SystemBooleanType))
            return raw?.Type == Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN && literal.Value is 0 or 1 ? 8 : 0;
        if (literal.Value is < int.MinValue or > uint.MaxValue)
            return 0;
        var integerStorage =
            ReferenceEquals(field.FieldType, types.SystemInt32Type) && raw?.Type == Il2CppTypeEnum.IL2CPP_TYPE_I4 ||
            ReferenceEquals(field.FieldType, types.SystemUInt32Type) && raw?.Type == Il2CppTypeEnum.IL2CPP_TYPE_U4;
        return integerStorage ? 32 : 0;
    }
}
