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
/// Authenticates one canonical Boolean byte store without replacing the caller.
/// The guard coalescer separately preserves the receiver check and effect order.
/// Missing metadata modifiers and this byte encoding do not establish the
/// original field's volatile declaration; that declaration uncertainty remains.
/// </summary>
internal static class BooleanLiteralFieldStoreProof
{
    internal static bool IsValidFor(MethodAnalysisContext method, Instruction operation,
        FieldReference access, Immediate literal)
    {
        var app = method.AppContext;
        var graph = method.ControlFlowGraph;
        var receiver = access.Local;
        var field = access.Field;
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) ||
            app.Binary is not PE pe || X64UnwindProof.ForApplication(app) is not { } unwind ||
            graph == null || !graph.Instructions.Contains(operation) ||
            operation is not { OpCode: OpCode.Move, IntegerBitWidth: 0,
                CallSemantics: CallSemantics.Direct, Operands: [FieldReference stored, Immediate value],
                NativeAddress: { } address } ||
            !ReferenceEquals(stored, access) || value.Value != literal.Value ||
            literal.Value is not (0 or 1) || receiver.Type == null ||
            !ReferenceEquals(field.FieldType, app.SystemTypes.SystemBooleanType) ||
            field.Visibility != FieldAttributes.Public ||
            (field.Attributes & FieldAttributes.InitOnly) != 0 ||
            field.BackingData?.Field.RawFieldType is not
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN, NumMods: 0, Byref: 0, Pinned: 0 } ||
            !NarrowFieldEqualityProof.HasUnchangedByteFieldLayout(access) ||
            method.Attributes != method.DefaultAttributes ||
            method.ImplAttributes != method.DefaultImplAttributes ||
            method.Name is ".ctor" or ".cctor" ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                requireUniqueBinding: false) || RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            OperandEffects.LocalsWithMutableStorage(graph.Instructions).Any(local =>
                local.Register.Number == receiver.Register.Number) ||
            !X64InlinedBooleanSetterProof.HasNativeCallerStore(method, pe, unwind,
                address, access, literal.Value == 1))
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

        // A copied result, phi, allocation or guessed native entry value needs
        // its own provenance proof. The call stays at its original location.
        if (definitions is not [var origin] ||
            graph.FindBlockByInstruction(origin) is not { } producerBlock ||
            graph.FindBlockByInstruction(operation) is not { } storeBlock ||
            !CallResultNullGuardProof.HasBoundProducer(method, receiver, origin))
            return false;

        // SSA destruction and later graph passes cannot defer the original
        // producer until after the store and its implicit null failure.
        return ReferenceEquals(producerBlock, storeBlock)
            ? producerBlock.Instructions.IndexOf(origin) < storeBlock.Instructions.IndexOf(operation)
            : new DominatorInfo(graph).Dominates(producerBlock, storeBlock);
    }
}
