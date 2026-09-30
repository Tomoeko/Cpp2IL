using System;
using System.IO;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.SourceEmission;

namespace Cpp2IL.Core.Tests;

[TestFixture]
public class UnityFloatingLiteralSourceFidelityTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp() => _directory = Path.Combine(Path.GetTempPath(), "cpp2il-nan-source-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [TestCase(32, "7FC00000")]
    [TestCase(32, "7FC00001")]
    [TestCase(32, "FFC00001")]
    [TestCase(32, "7F800001")]
    [TestCase(64, "7FF8000000000000")]
    [TestCase(64, "7FF8000000000001")]
    [TestCase(64, "FFF8000000000001")]
    [TestCase(64, "7FF0000000000001")]
    public void DifferentNaNSignSignalingOrPayloadCannotClaimCompleteSource(int width, string encoded)
    {
        var bits = Convert.ToUInt64(encoded, 16);
        var assembly = CreateAssembly(width, bits);
        var report = Emit(assembly);
        var source = File.ReadAllText(Path.Combine(_directory, report.Assemblies[0].SourceFile));
        Assert.That(source, Does.Contain(width == 32 ? "float.NaN" : "double.NaN"),
            "The source writer collapses distinct native NaN literals to the target constant.");
        Assert.That(report.Diagnostics, Has.Some.StartsWith("SOURCE015:"));
        Assert.That(report.SourceGeneration, Is.EqualTo("partial"));
        Assert.Throws<InvalidOperationException>(() => UnityCsOutputFormat.EnsureCompleteSourceGeneration(report));
    }

    [TestCase(32)]
    [TestCase(64)]
    public void CanonicalNaNRequiresMatchingResolvedReferenceBits(int width)
    {
        // This unit input explicitly targets the test runtime. Its supplied
        // compiler reference proves the constant; no Unity build is inferred.
        var bits = width == 32 ? unchecked((uint)BitConverter.SingleToInt32Bits(float.NaN)) :
            unchecked((ulong)BitConverter.DoubleToInt64Bits(double.NaN));
        var report = Emit(CreateAssembly(width, bits));
        Assert.That(report.Diagnostics, Is.Empty);
        Assert.That(report.SourceGeneration, Is.EqualTo("generated"));
        Assert.DoesNotThrow(() => UnityCsOutputFormat.EnsureCompleteSourceGeneration(report));
    }

    [TestCase(32, false)]
    [TestCase(32, true)]
    [TestCase(64, false)]
    [TestCase(64, true)]
    public void FieldAndParameterConstantsCannotLoseNaNPayload(int width, bool parameterDefault)
    {
        var assembly = CreateAssembly(width, 0);
        var module = assembly.ManifestModule!;
        var type = module.TopLevelTypes.Single(type => type.FullName == "Synthetic.NaNConstants");
        var valueType = width == 32 ? module.CorLibTypeFactory.Single : module.CorLibTypeFactory.Double;
        var value = NoncanonicalNaN(width);
        if (parameterDefault)
        {
            var method = type.Methods[0];
            method.Signature!.ParameterTypes.Add(valueType);
            method.ParameterDefinitions.Add(new ParameterDefinition(1, "value", ParameterAttributes.Optional | ParameterAttributes.HasDefault)
            {
                Constant = NaNConstant(value)
            });
            method.CilMethodBody!.Instructions[0].OpCode = CilOpCodes.Ldarg_0;
            method.CilMethodBody.Instructions[0].Operand = null;
        }
        else
            type.Fields.Add(new FieldDefinition("Constant", FieldAttributes.Public | FieldAttributes.Static |
                FieldAttributes.Literal | FieldAttributes.HasDefault, valueType) { Constant = NaNConstant(value) });
        RejectCollapsedNaN(assembly, width);
    }

    [TestCase(32, false)]
    [TestCase(32, true)]
    [TestCase(64, false)]
    [TestCase(64, true)]
    public void ScalarAndArrayAttributeArgumentsCannotLoseNaNPayload(int width, bool array)
    {
        var assembly = CreateAssembly(width, 0);
        var module = assembly.ManifestModule!;
        var attributeBase = new TypeReference(module, module.CorLibTypeFactory.CorLibScope, "System", "Attribute");
        var attribute = new TypeDefinition("Synthetic", "NaNMarkerAttribute", TypeAttributes.Public, attributeBase);
        module.TopLevelTypes.Add(attribute);
        TypeSignature valueType = width == 32 ? module.CorLibTypeFactory.Single : module.CorLibTypeFactory.Double;
        if (array) valueType = valueType.MakeSzArrayType();
        var constructor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName |
            MethodAttributes.RuntimeSpecialName, MethodSignature.CreateInstance(module.CorLibTypeFactory.Void, [valueType]));
        attribute.Methods.Add(constructor);
        constructor.CilMethodBody = new CilMethodBody();
        constructor.CilMethodBody.Instructions.Add(CilOpCodes.Ldarg_0);
        constructor.CilMethodBody.Instructions.Add(CilOpCodes.Call,
            attributeBase.CreateMemberReference(".ctor", MethodSignature.CreateInstance(module.CorLibTypeFactory.Void)));
        constructor.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        var value = NoncanonicalNaN(width);
        var argument = array ? new CustomAttributeArgument(valueType, new object[] { value }) :
            new CustomAttributeArgument(valueType, value);
        assembly.CustomAttributes.Add(new CustomAttribute(constructor) { Signature = new CustomAttributeSignature(argument) });
        RejectCollapsedNaN(assembly, width);
    }

    private void RejectCollapsedNaN(AssemblyDefinition assembly, int width)
    {
        var report = Emit(assembly);
        var source = File.ReadAllText(Path.Combine(_directory, report.Assemblies[0].SourceFile));
        Assert.That(source, Does.Contain(width == 32 ? "float.NaN" : "double.NaN"));
        Assert.That(report.Diagnostics, Has.Some.StartsWith("SOURCE015:"));
        Assert.That(report.SourceGeneration, Is.EqualTo("partial"));
        Assert.Throws<InvalidOperationException>(() => UnityCsOutputFormat.EnsureCompleteSourceGeneration(report));
    }

    private static object NoncanonicalNaN(int width) => width == 32 ?
        (object)BitConverter.Int32BitsToSingle(unchecked((int)0x7FC00001)) :
        BitConverter.Int64BitsToDouble(unchecked((long)0x7FF8000000000001));

    private static Constant NaNConstant(object value) => value is float single ? Constant.FromValue(single) :
        Constant.FromValue((double)value);

    private UnitySourceEmissionReport Emit(AssemblyDefinition assembly) =>
        UnitySourceProjectEmitter.Emit([assembly], ["Synthetic.NaNConstants"],
            [Path.GetDirectoryName(typeof(object).Assembly.Location)!], _directory);

    private static AssemblyDefinition CreateAssembly(int width, ulong bits)
    {
        var core = typeof(object).Assembly.GetName();
        var reference = new AssemblyReference(core.Name, core.Version!) { PublicKeyOrToken = core.GetPublicKeyToken() };
        var module = new ModuleDefinition("Synthetic.NaNConstants.dll", reference);
        var assembly = new AssemblyDefinition("Synthetic.NaNConstants", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        var type = new TypeDefinition("Synthetic", "NaNConstants", TypeAttributes.Public | TypeAttributes.Abstract |
            TypeAttributes.Sealed, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        var method = new MethodDefinition("Value", MethodAttributes.Public | MethodAttributes.Static,
            MethodSignature.CreateStatic(width == 32 ? module.CorLibTypeFactory.Single : module.CorLibTypeFactory.Double));
        type.Methods.Add(method);
        method.CilMethodBody = new CilMethodBody();
        if (width == 32) method.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_R4, BitConverter.Int32BitsToSingle(unchecked((int)bits)));
        else method.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_R8, BitConverter.Int64BitsToDouble(unchecked((long)bits)));
        method.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        return assembly;
    }
}
