using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;
using ReflectionMethodAttributes = System.Reflection.MethodAttributes;
using ReflectionTypeAttributes = System.Reflection.TypeAttributes;

namespace Cpp2IL.Core.Tests;

public partial class IlGeneratorParameterTests
{
    [Test]
    public void ValueTypeInstanceCallUsesAddressOfByValueParameterStorage()
    {
        var (valueType, target) = CreateIncrementingValueType();
        var (context, definition, parameters) = CreateMethod("IncrementCopy",
            _app.SystemTypes.SystemInt32Type, [valueType]);
        var result = new LocalVariable("result", new Register(1900, "result"),
            _app.SystemTypes.SystemInt32Type);
        context.Locals.Add(result);
        Emit(context, definition,
        [
            new(0, OpCode.Call, target, result, parameters[0]),
            new(1, OpCode.Return, result),
        ]);

        Assert.That(definition.CilMethodBody!.Instructions, Has.Some.Matches<CilInstruction>(
            instruction => instruction?.OpCode == CilOpCodes.Ldarga));
        using var runtime = Load();
        var structType = runtime.Type.Assembly.GetType("Synthetic.Counter")!;
        var original = Activator.CreateInstance(structType)!;
        structType.GetField("Value")!.SetValue(original, 41);
        Assert.That(runtime.Type.GetMethod("IncrementCopy")!.Invoke(null, [original]), Is.EqualTo(42));
        Assert.That(structType.GetField("Value")!.GetValue(original), Is.EqualTo(41),
            "A by-value argument must not mutate the caller's boxed value.");
    }

    [Test]
    public void ValueTypeInstanceCallRejectsUnprovedSsaCopyStorage()
    {
        var (valueType, target) = CreateIncrementingValueType();
        var (context, definition, _) = CreateMethod("UnknownCopy",
            _app.SystemTypes.SystemInt32Type, []);
        var copy = new LocalVariable("copy", new Register(1901, "copy"), valueType);
        var result = new LocalVariable("result", new Register(1902, "result"),
            _app.SystemTypes.SystemInt32Type);
        context.Locals.Add(copy);
        context.Locals.Add(result);
        Assert.That(() => Emit(context, definition,
        [
            new(0, OpCode.Call, target, result, copy),
            new(1, OpCode.Return, result),
        ]), Throws.TypeOf<DecompilerException>().With.Message.Contains("no proved managed storage"));
    }

    private (InjectedTypeAnalysisContext ValueType, InjectedMethodAnalysisContext Target)
        CreateIncrementingValueType()
    {
        var valueType = new InjectedTypeAnalysisContext(_typeContext.DeclaringAssembly,
            "Synthetic", "Counter", _app.SystemTypes.SystemValueTypeType,
            ReflectionTypeAttributes.Public | ReflectionTypeAttributes.Sealed |
            ReflectionTypeAttributes.SequentialLayout);
        var valueTypeDefinition = new TypeDefinition("Synthetic", "Counter",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
            _module.DefaultImporter.ImportTypeSignature(
                _app.SystemTypes.SystemValueTypeType.ToTypeSignature()).ToTypeDefOrRef());
        _module.TopLevelTypes.Add(valueTypeDefinition);
        valueType.PutExtraData("AsmResolverType", valueTypeDefinition);
        var valueField = new FieldDefinition("Value", FieldAttributes.Public,
            _module.CorLibTypeFactory.Int32);
        valueTypeDefinition.Fields.Add(valueField);

        var target = new InjectedMethodAnalysisContext(valueType, "Increment",
            _app.SystemTypes.SystemInt32Type, ReflectionMethodAttributes.Public, []);
        var method = new MethodDefinition("Increment", MethodAttributes.Public,
            MethodSignature.CreateInstance(_module.CorLibTypeFactory.Int32))
        {
            CilMethodBody = new CilMethodBody(),
        };
        valueTypeDefinition.Methods.Add(method);
        target.PutExtraData("AsmResolverMethod", method);
        var il = method.CilMethodBody.Instructions;
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Dup);
        il.Add(CilOpCodes.Ldfld, valueField);
        il.Add(CilOpCodes.Ldc_I4_1);
        il.Add(CilOpCodes.Add);
        il.Add(CilOpCodes.Stfld, valueField);
        il.Add(CilOpCodes.Ldarg_0);
        il.Add(CilOpCodes.Ldfld, valueField);
        il.Add(CilOpCodes.Ret);
        return (valueType, target);
    }
}
