using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using ICSharpCode.Decompiler.TypeSystem;

namespace Cpp2IL.Core.SourceEmission;

internal static class UnityFloatingLiteralSourceFidelity
{
    // The source writer represents NaN literals as Single.NaN or Double.NaN.
    // Floating equality cannot authenticate that substitution: it loses the
    // sign, signaling bit and payload. Read the target reference's constant.
    internal static void Validate(ModuleDefinition module, ICompilation target, List<string> diagnostics)
    {
        var targetBits = new Dictionary<KnownTypeCode, ulong?>();
        var diagnosed = new HashSet<string>(StringComparer.Ordinal);
        CheckProvider(module, module.Name?.ToString() ?? "module");
        if (module.Assembly is { } assembly) CheckProvider(assembly, assembly.Name!.ToString());
        foreach (var type in module.GetAllTypes())
        {
            CheckProvider(type, type.FullName);
            foreach (var parameter in type.GenericParameters) CheckProvider(parameter, type.FullName);
            foreach (var field in type.Fields) CheckProvider(field, field.FullName);
            foreach (var property in type.Properties) CheckProvider(property, property.FullName);
            foreach (var @event in type.Events) CheckProvider(@event, @event.FullName);
            foreach (var method in type.Methods)
            {
                CheckProvider(method, method.FullName);
                foreach (var parameter in method.GenericParameters) CheckProvider(parameter, method.FullName);
                foreach (var parameter in method.ParameterDefinitions) CheckProvider(parameter, method.FullName);
                if (method.CilMethodBody is not { } body) continue;
                foreach (var instruction in body.Instructions)
                    if (instruction.OpCode == CilOpCodes.Ldc_R4 || instruction.OpCode == CilOpCodes.Ldc_R8)
                        CheckValue(instruction.Operand, method.FullName);
            }
        }

        void CheckProvider(IHasCustomAttribute provider, string origin)
        {
            if (provider is IHasConstant { Constant: { } constant })
                CheckValue(constant.InterpretData(), origin);
            if (provider is GenericParameter parameter)
                foreach (var constraint in parameter.Constraints) CheckProvider(constraint, origin);
            foreach (var attribute in provider.CustomAttributes)
            {
                if (attribute.Signature is not { } signature) continue;
                foreach (var argument in signature.FixedArguments)
                    foreach (var value in argument.Elements) CheckValue(value, origin);
                foreach (var argument in signature.NamedArguments)
                    foreach (var value in argument.Argument.Elements) CheckValue(value, origin);
            }
        }

        void CheckValue(object? value, string origin)
        {
            KnownTypeCode kind;
            ulong bits;
            switch (value)
            {
                case BoxedArgument boxed:
                    CheckValue(boxed.Value, origin);
                    return;
                case Array array:
                    foreach (var item in array) CheckValue(item, origin);
                    return;
                case IEnumerable<object?> values:
                {
                    foreach (var item in values) CheckValue(item, origin);
                    return;
                }
                case float single when float.IsNaN(single):
                    kind = KnownTypeCode.Single;
                    bits = BitConverter.ToUInt32(BitConverter.GetBytes(single), 0);
                    break;
                case double wide when double.IsNaN(wide):
                    kind = KnownTypeCode.Double;
                    bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(wide));
                    break;
                default:
                    return;
            }
            if (!targetBits.TryGetValue(kind, out var emitted))
                targetBits.Add(kind, emitted = TargetNaNBits(target, kind));
            if (emitted != bits && diagnosed.Add(origin))
                diagnostics.Add($"SOURCE015: {module.Assembly?.Name}: {origin}: {kind} NaN literal bits are not established by the resolved target NaN constant; source emission cannot preserve the literal's sign, signaling bit and payload.");
        }
    }

    private static ulong? TargetNaNBits(ICompilation target, KnownTypeCode type)
    {
        var fields = target.FindType(type).GetDefinition()?.Fields
            .Where(field => field.Name == "NaN" && field.IsStatic && field.IsConst &&
                field.Accessibility == Accessibility.Public && field.ReturnType.IsKnownType(type)).ToArray();
        if (fields is not [{ } field])
            return null;
        return (type, field.GetConstantValue(true)) switch
        {
            (KnownTypeCode.Single, float value) when float.IsNaN(value) =>
                BitConverter.ToUInt32(BitConverter.GetBytes(value), 0),
            (KnownTypeCode.Double, double value) when double.IsNaN(value) =>
                unchecked((ulong)BitConverter.DoubleToInt64Bits(value)),
            _ => null,
        };
    }
}
