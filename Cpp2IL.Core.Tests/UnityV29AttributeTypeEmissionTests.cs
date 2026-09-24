using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AssetRipper.Primitives;
using Cpp2IL.Core.Model.CustomAttributes;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;
using Cpp2IL.Core.SourceEmission;
using Cpp2IL.Core.Utils.AsmResolver;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Tests;

[TestFixture]
[NonParallelizable]
public class UnityV29AttributeTypeEmissionTests
{
    [Test]
    public void IndexedTypeValuesAndRetainedSiblingsRequireExactEmission()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ATTRIBUTE_PARAMETER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ATTRIBUTE_PARAMETER_FIXTURE_INPUT to the neutral exact-version attribute player input.");

        var binary = Path.Combine(directory, "GameAssembly.dll");
        var metadata = Path.Combine(directory, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True, "Missing exact native fixture inputs.");

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            Assert.That(app.MetadataVersion, Is.EqualTo(29));
            new AttributeAnalysisProcessingLayer().Process(app);
            var managed = new AsmResolverDllOutputFormatDefault().BuildAssemblies(app)
                .Single(assembly => assembly.Name == "AttributeParameterFixture");
            var sourceType = app.GetAssemblyByName("AttributeParameterFixture")!
                .GetTypeByFullName("AttributeParameterFixture.PayloadCases")!;
            var emittedType = managed.ManifestModule!.GetAllTypes().Single(type => type.Name == "PayloadCases");
            var sourceAttributes = sourceType.CustomAttributes!;
            var emittedAttributes = emittedType.CustomAttributes;
            var assessment = UnityV29AttributeTypeProvenance.AssessEmission(app, ["AttributeParameterFixture"]).Single();
            Assert.That(assessment.IndexedValueCount, Is.GreaterThanOrEqualTo(5));
            Assert.That(assessment.RetainedArgumentsEmitted, Is.True);

            var typeArrayIndex = sourceAttributes.FindIndex(attribute => attribute.ConstructorParameters.SingleOrDefault() is
                CustomAttributeArrayParameter { ArrType: Il2CppTypeEnum.IL2CPP_TYPE_IL2CPP_TYPE_INDEX });
            var objectArrayIndex = sourceAttributes.FindIndex(attribute => attribute.ConstructorParameters.SingleOrDefault() is
                CustomAttributeArrayParameter { ArrType: Il2CppTypeEnum.IL2CPP_TYPE_OBJECT } array &&
                array.ArrayElements.Any(element => element is CustomAttributeTypeParameter { HasNonNullV29TypeIndex: true }));
            var namedIndex = sourceAttributes.FindIndex(attribute => attribute.Fields.Any(field =>
                field.Value is CustomAttributeTypeParameter { HasNonNullV29TypeIndex: true }));
            Assert.That(typeArrayIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(objectArrayIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(namedIndex, Is.GreaterThanOrEqualTo(0));

            var typeArraySource = (CustomAttributeArrayParameter)sourceAttributes[typeArrayIndex].ConstructorParameters[0];
            var typeArrayEmitted = emittedAttributes[typeArrayIndex];
            var typeArraySignature = typeArrayEmitted.Signature!;
            var typeArrayBox = (BoxedArgument)typeArraySignature.FixedArguments[0].Element!;
            var typeArrayValues = ((object[])typeArrayBox.Value!).ToArray();
            Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(sourceAttributes[typeArrayIndex], typeArrayEmitted), Is.True);
            try
            {
                var firstIndexedType = (CustomAttributeTypeParameter)typeArraySource.ArrayElements[0];
                var typeField = typeof(CustomAttributeTypeParameter).GetField("_type", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var contextField = typeof(CustomAttributeTypeParameter).GetField("_typeContext", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var originalType = typeField.GetValue(firstIndexedType);
                var originalContext = contextField.GetValue(firstIndexedType);
                try
                {
                    typeField.SetValue(firstIndexedType, null);
                    contextField.SetValue(firstIndexedType, null);
                    Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(sourceAttributes[typeArrayIndex], typeArrayEmitted), Is.False,
                        "An indexed Type with an unresolved context cannot certify emission.");
                }
                finally
                {
                    typeField.SetValue(firstIndexedType, originalType);
                    contextField.SetValue(firstIndexedType, originalContext);
                }

                typeArrayEmitted.Signature = WithFixed(typeArraySignature,
                    new CustomAttributeArgument(typeArraySignature.FixedArguments[0].ArgumentType,
                        new BoxedArgument(typeArrayBox.Type, Array.Empty<object>())));
                Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(sourceAttributes[typeArrayIndex], typeArrayEmitted), Is.False,
                    "An empty array cannot replace the indexed Type leaves.");
                typeArrayEmitted.Signature = WithFixed(typeArraySignature,
                    new CustomAttributeArgument(typeArraySignature.FixedArguments[0].ArgumentType,
                        new BoxedArgument(app.SystemTypes.SystemTypeType.ToTypeSignature(), typeArrayValues)));
                Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(sourceAttributes[typeArrayIndex], typeArrayEmitted), Is.False,
                    "A boxed Type cannot replace a boxed Type array.");
            }
            finally { typeArrayEmitted.Signature = typeArraySignature; }

            var objectArraySource = (CustomAttributeArrayParameter)sourceAttributes[objectArrayIndex].ConstructorParameters[0];
            var objectArrayEmitted = emittedAttributes[objectArrayIndex];
            var objectArraySignature = objectArrayEmitted.Signature!;
            var objectArrayBox = (BoxedArgument)objectArraySignature.FixedArguments[0].Element!;
            var objectArrayValues = ((object[])objectArrayBox.Value!).ToArray();
            var indexedPosition = objectArraySource.ArrayElements.FindIndex(element =>
                element is CustomAttributeTypeParameter { HasNonNullV29TypeIndex: true });
            Assert.That(indexedPosition, Is.GreaterThanOrEqualTo(0));
            try
            {
                // A direct object[] may begin with a boxed Type. Its first Element is not
                // a box around the whole array.
                (objectArraySource.ArrayElements[0], objectArraySource.ArrayElements[indexedPosition]) =
                    (objectArraySource.ArrayElements[indexedPosition], objectArraySource.ArrayElements[0]);
                (objectArrayValues[0], objectArrayValues[indexedPosition]) =
                    (objectArrayValues[indexedPosition], objectArrayValues[0]);
                objectArrayEmitted.Signature = WithFixed(objectArraySignature,
                    new CustomAttributeArgument(objectArraySignature.FixedArguments[0].ArgumentType,
                        new BoxedArgument(objectArrayBox.Type, objectArrayValues)));
                Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(sourceAttributes[objectArrayIndex], objectArrayEmitted), Is.True);
                var directObjectArray = new CustomAttributeArgument(objectArrayBox.Type, objectArrayValues);
                Assert.That(UnityV29AttributeTypeProvenance.MatchesRetainedArgument(objectArraySource, directObjectArray, objectArrayBox.Type), Is.True,
                    "A direct object[] can start with a boxed Type without boxing the whole array.");
                Assert.That(UnityV29AttributeTypeProvenance.MatchesRetainedArgument(objectArraySource,
                    new CustomAttributeArgument(objectArrayBox.Type, Array.Empty<object>()), objectArrayBox.Type), Is.False);
                Assert.That(UnityV29AttributeTypeProvenance.MatchesRetainedArgument(objectArraySource,
                    new CustomAttributeArgument(objectArrayBox.Type), objectArrayBox.Type), Is.False);

                var wrongType = app.SystemTypes.SystemInt32Type.ToTypeSignature();
                var originalTypeBox = (BoxedArgument)objectArrayValues[0];
                objectArrayValues[0] = new BoxedArgument(originalTypeBox.Type, wrongType);
                objectArrayEmitted.Signature = WithFixed(objectArraySignature,
                    new CustomAttributeArgument(objectArraySignature.FixedArguments[0].ArgumentType,
                        new BoxedArgument(objectArrayBox.Type, objectArrayValues)));
                Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(sourceAttributes[objectArrayIndex], objectArrayEmitted), Is.False,
                    "A wrong nested Type is not the indexed identity.");
                Assert.That(UnityV29AttributeTypeProvenance.MatchesRetainedArgument(objectArraySource,
                    new CustomAttributeArgument(objectArrayBox.Type, objectArrayValues), objectArrayBox.Type), Is.False,
                    "A wrong nested Type is rejected in a direct object[] too.");

                objectArrayValues[0] = originalTypeBox;
                objectArrayEmitted.Signature = WithFixed(objectArraySignature,
                    new CustomAttributeArgument(objectArraySignature.FixedArguments[0].ArgumentType,
                        new BoxedArgument(objectArrayBox.Type, objectArrayValues)));
                Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(sourceAttributes[objectArrayIndex], objectArrayEmitted), Is.True);

                var bytePosition = objectArraySource.ArrayElements.FindIndex(element =>
                    element is CustomAttributePrimitiveParameter { PrimitiveType: Il2CppTypeEnum.IL2CPP_TYPE_U1 });
                var enumPosition = objectArraySource.ArrayElements.FindIndex(element => element is CustomAttributeEnumParameter);
                var nullPosition = objectArraySource.ArrayElements.FindIndex(element =>
                    element is CustomAttributePrimitiveParameter
                        { PrimitiveType: Il2CppTypeEnum.IL2CPP_TYPE_STRING, PrimitiveValue: null });
                Assert.That(bytePosition, Is.GreaterThanOrEqualTo(0));
                Assert.That(enumPosition, Is.GreaterThanOrEqualTo(0));
                Assert.That(nullPosition, Is.GreaterThanOrEqualTo(0));

                var byteBox = (BoxedArgument)objectArrayValues[bytePosition];
                var enumBox = (BoxedArgument)objectArrayValues[enumPosition];
                var nullBox = (BoxedArgument)objectArrayValues[nullPosition];
                AssertMutationRejected(bytePosition, new BoxedArgument(byteBox.Type, (byte)8),
                    "A changed retained byte cannot accompany a correct indexed Type.");
                AssertMutationRejected(bytePosition,
                    new BoxedArgument(app.SystemTypes.SystemInt32Type.ToTypeSignature(), byteBox.Value),
                    "A changed primitive box type cannot accompany a correct indexed Type.");
                AssertMutationRejected(enumPosition, new BoxedArgument(enumBox.Type, (byte)0),
                    "A changed retained enum value cannot accompany a correct indexed Type.");
                AssertMutationRejected(enumPosition,
                    new BoxedArgument(app.SystemTypes.SystemByteType.ToTypeSignature(), enumBox.Value),
                    "An enum cannot be replaced by its underlying primitive box type.");
                AssertMutationRejected(nullPosition, new BoxedArgument(nullBox.Type, "changed"),
                    "A retained typed null cannot be replaced by a string value.");
                AssertMutationRejected(nullPosition,
                    new BoxedArgument(app.SystemTypes.SystemObjectType.ToTypeSignature(), null),
                    "A retained typed null cannot change its box type.");

                var retainedNull = objectArraySource.ArrayElements[nullPosition];
                try
                {
                    objectArraySource.ArrayElements[nullPosition] = new CustomAttributeNullParameter(
                        retainedNull.Owner, retainedNull.Kind, retainedNull.Index);
                    Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(
                        sourceAttributes[objectArrayIndex], objectArrayEmitted), Is.False,
                        "An untyped null without a retained box type cannot certify emission.");
                }
                finally { objectArraySource.ArrayElements[nullPosition] = retainedNull; }

                void AssertMutationRejected(int position, BoxedArgument replacement, string reason)
                {
                    var values = objectArrayValues.ToArray();
                    values[position] = replacement;
                    var originalSignature = objectArrayEmitted.Signature;
                    try
                    {
                        objectArrayEmitted.Signature = WithFixed(objectArraySignature,
                            new CustomAttributeArgument(objectArraySignature.FixedArguments[0].ArgumentType,
                                new BoxedArgument(objectArrayBox.Type, values)));
                        Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(
                            sourceAttributes[objectArrayIndex], objectArrayEmitted), Is.False, reason);
                        Assert.That(UnityV29AttributeTypeProvenance.AssessEmission(app,
                            ["AttributeParameterFixture"]).Single().RetainedArgumentsEmitted, Is.False, reason);
                    }
                    finally { objectArrayEmitted.Signature = originalSignature; }
                }
            }
            finally
            {
                (objectArraySource.ArrayElements[0], objectArraySource.ArrayElements[indexedPosition]) =
                    (objectArraySource.ArrayElements[indexedPosition], objectArraySource.ArrayElements[0]);
                objectArrayEmitted.Signature = objectArraySignature;
            }

            var namedSource = sourceAttributes[namedIndex];
            var namedEmitted = emittedAttributes[namedIndex];
            var namedSignature = namedEmitted.Signature!;
            var target = namedSignature.NamedArguments.Single(argument => argument.MemberName == "Target");
            var originalMemberName = target.MemberName;
            var originalTargetArgument = target.Argument;
            Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(namedSource, namedEmitted), Is.True);
            try
            {
                var label = namedSignature.NamedArguments.Single(argument => argument.MemberName == "Label");
                var originalLabelArgument = label.Argument;
                try
                {
                    label.Argument = new CustomAttributeArgument(originalLabelArgument.ArgumentType, "changed");
                    Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(namedSource, namedEmitted), Is.False,
                        "A separate retained named string must match even when its Type sibling is correct.");
                    Assert.That(UnityV29AttributeTypeProvenance.AssessEmission(app,
                        ["AttributeParameterFixture"]).Single().RetainedArgumentsEmitted, Is.False);
                }
                finally { label.Argument = originalLabelArgument; }

                var wrongScope = new AssemblyReference("Synthetic.WrongScope", new Version(1, 0, 0, 0));
                var wrongType = new TypeReference(managed.ManifestModule, wrongScope,
                    "AttributeParameterFixture", "ParameterCases").ToTypeSignature(false);
                target.Argument = new CustomAttributeArgument(originalTargetArgument.ArgumentType, wrongType);
                Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(namedSource, namedEmitted), Is.False,
                    "A same-named Type from another assembly is not the indexed identity.");
                target.Argument = originalTargetArgument;
                target.MemberName = "Other";
                Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(namedSource, namedEmitted), Is.False);
            }
            finally
            {
                target.Argument = originalTargetArgument;
                target.MemberName = originalMemberName;
            }

            var nullArrayIndex = sourceAttributes.FindIndex(attribute =>
                attribute.ConstructorParameters.SingleOrDefault() is
                    CustomAttributeTypeParameter { HasNonNullV29TypeIndex: true } &&
                attribute.Fields.Any(field => field.Field.Name == "Numbers" &&
                                              field.Value is CustomAttributeArrayParameter { IsNullArray: true }));
            Assert.That(nullArrayIndex, Is.GreaterThanOrEqualTo(0));
            var nullArraySource = sourceAttributes[nullArrayIndex];
            var nullArrayEmitted = emittedAttributes[nullArrayIndex];
            var numbers = nullArrayEmitted.Signature!.NamedArguments.Single(argument => argument.MemberName == "Numbers");
            var originalNumbers = numbers.Argument;
            Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(nullArraySource, nullArrayEmitted), Is.True);
            try
            {
                numbers.Argument = new CustomAttributeArgument(originalNumbers.ArgumentType, Array.Empty<object>());
                Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(nullArraySource, nullArrayEmitted), Is.False,
                    "An empty array cannot replace a retained null array.");
                numbers.Argument = new CustomAttributeArgument(app.SystemTypes.SystemByteType.ToTypeSignature().MakeSzArrayType())
                    { IsNullArray = true };
                Assert.That(UnityV29AttributeTypeProvenance.HasMatchingRetainedArguments(nullArraySource, nullArrayEmitted), Is.False,
                    "The null array's emitted element type must match its declared owner.");
            }
            finally { numbers.Argument = originalNumbers; }

            // The writer silently drops an unsuitable attribute. The downgrade must fail
            // closed if the source and emitted owner collections cease to correspond.
            var removed = emittedAttributes[namedIndex];
            emittedAttributes.RemoveAt(namedIndex);
            try
            {
                Assert.That(UnityV29AttributeTypeProvenance.AssessEmission(app, ["AttributeParameterFixture"]).Single().RetainedArgumentsEmitted, Is.False);
            }
            finally { emittedAttributes.Insert(namedIndex, removed); }

            var sourceArgument = namedSource.ConstructorParameters.Single();
            namedSource.ConstructorParameters.Clear();
            try
            {
                Assert.That(UnityV29AttributeTypeProvenance.AssessEmission(app, ["AttributeParameterFixture"]).Single().RetainedArgumentsEmitted, Is.False);
            }
            finally { namedSource.ConstructorParameters.Add(sourceArgument); }
            Assert.That(UnityV29AttributeTypeProvenance.AssessEmission(app, ["AttributeParameterFixture"]).Single().RetainedArgumentsEmitted, Is.True);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static CustomAttributeSignature WithFixed(CustomAttributeSignature source, CustomAttributeArgument fixedArgument) =>
        new([fixedArgument], source.NamedArguments);
}
