using System.IO;
using System.Linq;
using Cpp2IL.Core.Model.CustomAttributes;
using Cpp2IL.Core.SourceEmission;

namespace Cpp2IL.Core.Tests;

[TestFixture]
public class UnityV29AttributeTypeProvenanceTests
{
    [SetUp]
    public void SetUp()
    {
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.LoadSimple2022Game();
    }

    [Test]
    public void NonNullPlayerTypeIndexIsFoundThroughNestedAndNamedAttributeValues()
    {
        var context = Cpp2IlApi.CurrentAppContext!;
        var assembly = context.GetAssemblyByName("mscorlib")!;
        var type = assembly.GetTypeByFullName("System.Collections.Generic.List`1")!;
        type.AnalyzeCustomAttributeData();
        var decoded = type.CustomAttributes!.Single(attribute =>
            attribute.Constructor.DeclaringType!.FullName == "System.Diagnostics.DebuggerTypeProxyAttribute");
        var indexedType = (CustomAttributeTypeParameter)decoded.ConstructorParameters.Single();
        Assert.That(indexedType.HasNonNullV29TypeIndex, Is.True);
        Assert.That(UnityV29AttributeTypeProvenance.GetAffectedAssemblyNames(context, ["mscorlib"]).ToArray(),
            Is.EqualTo(new[] { "mscorlib" }));

        var authored = new AnalyzedCustomAttribute(decoded.Constructor);
        authored.ConstructorParameters.Add(new CustomAttributeTypeParameter(indexedType.TypeContext, authored,
            CustomAttributeParameterKind.ConstructorParam, 0));
        var nullValue = new CustomAttributeTypeParameter(authored, CustomAttributeParameterKind.ConstructorParam, 1);
        using (var reader = new BinaryReader(new MemoryStream([1]))) // v29 compressed -1
            nullValue.ReadFromV29Blob(reader, context);
        authored.ConstructorParameters.Add(nullValue);
        Assert.That(nullValue.HasNonNullV29TypeIndex, Is.False);
        Assert.That(UnityV29AttributeTypeProvenance.ContainsTypeValueWithUnknownSerialization([authored]), Is.False);

        var nullArray = new CustomAttributeArrayParameter(authored, CustomAttributeParameterKind.ConstructorParam, 2)
        {
            IsNullArray = true,
            ArrayElements = [indexedType],
        };
        authored.ConstructorParameters.Add(nullArray);
        Assert.That(UnityV29AttributeTypeProvenance.ContainsTypeValueWithUnknownSerialization([authored]), Is.False);

        var nestedTypes = new CustomAttributeArrayParameter(authored, CustomAttributeParameterKind.ArrayElement, 0)
        {
            ArrayElements = [indexedType],
        };
        var boxedValues = new CustomAttributeArrayParameter(authored, CustomAttributeParameterKind.ConstructorParam, 3)
        {
            ArrayElements = [nestedTypes],
        };
        authored.ConstructorParameters.Add(boxedValues);
        Assert.That(UnityV29AttributeTypeProvenance.ContainsTypeValueWithUnknownSerialization([authored]), Is.True);

        authored.ConstructorParameters.Clear();
        authored.Fields.Add(new(type.Fields.First(), boxedValues));
        Assert.That(UnityV29AttributeTypeProvenance.ContainsTypeValueWithUnknownSerialization([authored]), Is.True);
        authored.Fields.Clear();
        authored.Properties.Add(new(type.Properties.First(), boxedValues));
        Assert.That(UnityV29AttributeTypeProvenance.ContainsTypeValueWithUnknownSerialization([authored]), Is.True);
    }
}
