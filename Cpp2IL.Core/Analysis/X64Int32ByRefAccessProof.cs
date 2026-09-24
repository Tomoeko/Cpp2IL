using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Recognizes a direct, full-width access through an unchanged managed ref Int32
/// parameter in the Windows x64 ABI. An SSA copy or rewritten argument register
/// does not establish the identity of the pointed-to managed element.
/// </summary>
internal static class X64Int32ByRefAccessProof
{
    internal static bool IsNativeAccess(MethodAnalysisContext method, Iced.Intel.Instruction native)
    {
        if (native.Mnemonic != Mnemonic.Mov || native.MemorySize.GetSize() != 4 ||
            native.MemoryIndex != Iced.Intel.Register.None || native.MemoryDisplacement64 != 0 ||
            native.MemoryBase == Iced.Intel.Register.None ||
            native.Op0Kind != OpKind.Memory && native.Op1Kind != OpKind.Memory)
            return false;

        var registerName = X86Utils.GetRegisterName(native.MemoryBase.GetFullRegister());
        return ParameterIndex(method, registerName) >= 0;
    }

    internal static bool IsManagedAccess(MethodAnalysisContext method, ISIL.MemoryOperand memory)
    {
        if (memory is not { Index: null, Scale: 0, Addend: 0,
                Base: LocalVariable { Type: ByRefTypeAnalysisContext { ElementType: { } referent } } baseLocal } ||
            baseLocal.Register.Version != -1 ||
            !ReferenceEquals(referent, method.AppContext.SystemTypes.SystemInt32Type) ||
            !method.ParameterLocals.Contains(baseLocal))
            return false;

        var ordinal = ParameterIndex(method, baseLocal.Register.Name);
        if (ordinal < 0)
            return false;
        var operandIndex = ordinal + (method.IsStatic ? 0 : 1);
        return operandIndex < method.ParameterOperands.Count &&
               method.ParameterOperands[operandIndex] is ISIL.Register native &&
               native == baseLocal.Register;
    }

    private static int ParameterIndex(MethodAnalysisContext method, string registerName)
    {
        if (method.AppContext.Binary is not PE { PointerSizeBytes: 8 } pe ||
            pe.InstructionSetId != DefaultInstructionSets.X86_64 ||
            method.Definition is not { } definition ||
            definition.InternalParameterData is not { } rawParameters ||
            rawParameters.Length != method.Parameters.Count ||
            definition.parameterCount != method.Parameters.Count ||
            method.UnderlyingPointer == 0 ||
            !method.AppContext.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
            bindings.Count != 1 || !ReferenceEquals(bindings[0], method) ||
            new X64CallingConventionResolver().ReturnsViaHiddenBuffer(method))
            return -1;

        var operands = new X64CallingConventionResolver().ResolveForManaged(method);
        for (var i = 0; i < rawParameters.Length; i++)
        {
            var operandIndex = i + (method.IsStatic ? 0 : 1);
            if (operandIndex >= operands.Length ||
                operands[operandIndex] is not ISIL.Register { Name: var name } ||
                name != registerName)
                continue;

            var parameter = method.Parameters[i];
            if (parameter.ParameterIndex != i ||
                !ReferenceEquals(parameter.DeclaringMethod, method) ||
                !ReferenceEquals(parameter.Definition, rawParameters[i]) ||
                rawParameters[i].RawType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4,
                    Byref: 1, NumMods: 0, Pinned: 0 } ||
                parameter.ParameterType is not ByRefTypeAnalysisContext current ||
                parameter.DefaultParameterType is not ByRefTypeAnalysisContext original ||
                !ReferenceEquals(current.ElementType, method.AppContext.SystemTypes.SystemInt32Type) ||
                !ReferenceEquals(current.ElementType, original.ElementType) ||
                parameter.Attributes != parameter.DefaultAttributes ||
                parameter.OverrideParameterType != null ||
                parameter.OverrideAttributes != null ||
                parameter.UseOverrideDefaultValue ||
                parameter.Name != parameter.DefaultName)
                return -1;
            return i;
        }
        return -1;
    }
}
