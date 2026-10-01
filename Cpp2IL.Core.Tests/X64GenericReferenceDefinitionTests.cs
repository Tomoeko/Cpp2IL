using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Reporting;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64GenericReferenceDefinitionTests
{
    private ApplicationAnalysisContext _app = null!;
    private MethodAnalysisContext _method = null!;
    private X64GenericReferenceDefinitionProof.Evidence _saved = null!;

    [OneTimeSetUp]
    public void LoadOriginalNeutralPlayer()
    {
        const string variable = "CPP2IL_NATIVE_DIRECT_GENERIC_REFERENCE_INVOCATION_FIXTURE_INPUT";
        var directory = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set " + variable + " to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Directory.EnumerateFiles(directory!, "global-metadata.dat", SearchOption.AllDirectories).Single(),
            UnityVersion.Parse("2021.3.35f1"));
        _app = Cpp2IlApi.CurrentAppContext!;
        _method = _app.GetAssemblyByName("NativeDirectGenericReferenceInvocationFixture")!
            .Types.Single(type => type.Name == "Relay").Methods.Single(method => method.Name == "Echo");
        _saved = Accept();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void OriginalDefinitionAndEverySelectedSpecificationRemainDistinct()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_saved.OriginalDefinitionModulePointer, Is.Zero);
            Assert.That(_saved.Specifications.Length, Is.EqualTo(3));
            Assert.That(_saved.Specifications.ToArray().Count(spec => spec.DirectlyRegistered), Is.EqualTo(1));
            Assert.That(_saved.Variants.Length, Is.EqualTo(1));
            Assert.That(_saved.Declaration.Parameters[0].Context, Is.SameAs(_saved.GenericParameter));
            Assert.That(_saved.GenericParameter.Definition, Is.SameAs(_saved.Declaration.Definition.GenericParameters.Single()));
            Assert.That(_saved.CompleteBodySemantics, Is.False);
        });
        Assert.That(_saved.UnresolvedBodyReasons, Has.Length.EqualTo(3));
        Assert.That(_saved.UnresolvedBodyReasons.Any(reason => reason.Contains("helper") || reason.Contains("cache")), Is.False,
            "Caller initialization effects are not body-local premises of a call-free leaf.");
    }

    [TestCase("original-container-replacement")]
    [TestCase("original-parameter-replacement")]
    [TestCase("generic-context-without-original-row")]
    [TestCase("null-generic-context")]
    [TestCase("class-constraint")]
    [TestCase("signature-return")]
    [TestCase("signature-parameter")]
    [TestCase("counter-type")]
    [TestCase("counter-offset")]
    [TestCase("counter-readonly")]
    [TestCase("managed-alias-missing")]
    [TestCase("managed-alias-duplicate")]
    [TestCase("native-alias-missing")]
    [TestCase("registered-pointer")]
    [TestCase("concrete-return")]
    [TestCase("interior-entry")]
    [TestCase("cached-native-body")]
    [TestCase("native-padding")]
    [TestCase("native-field-offset")]
    [TestCase("native-size-pointer")]
    public void ChangedIdentityAbiAliasesOrNativeExtentCannotProducePartialIl(string defect)
    {
        var undo = new Stack<Action>();
        var pe = (PE)_app.Binary;
        var variant = _saved.Variants[0];
        try
        {
            void Assign<T>(Func<T> read, Action<T> write, T value)
            {
                var previous = read();
                undo.Push(() => write(previous));
                write(value);
            }
            switch (defect)
            {
                case "original-container-replacement":
                    var containers = (Il2CppGenericContainer[])PrivateField(typeof(Il2CppMetadata), "genericContainers").GetValue(_app.Metadata)!;
                    var ordinal = _saved.Declaration.Container.Index;
                    Assign(() => containers[ordinal], value => containers[ordinal] = value, Clone(containers[ordinal]));
                    break;
                case "original-parameter-replacement":
                    var parameters = (Il2CppGenericParameter[])PrivateField(typeof(Il2CppMetadata), "genericParameters").GetValue(_app.Metadata)!;
                    var position = _saved.Declaration.Parameters[0].Origin.Index;
                    Assign(() => parameters[position], value => parameters[position] = value, Clone(parameters[position]));
                    break;
                case "generic-context-without-original-row":
                    var generic = _saved.GenericParameter;
                    Assign(() => _method.GenericParameters[0], value => _method.GenericParameters[0] = value,
                        new GenericParameterTypeAnalysisContext(generic.Name, generic.Index, generic.Type, generic.Attributes, _method));
                    break;
                case "null-generic-context": Assign(() => _method.GenericParameters[0], value => _method.GenericParameters[0] = value, null!); break;
                case "class-constraint": Assign(() => _saved.GenericParameter.OverrideAttributes, value => _saved.GenericParameter.OverrideAttributes = value, (GenericParameterAttributes?)GenericParameterAttributes.None); break;
                case "signature-return": Assign(() => _method.OverrideReturnType, value => _method.OverrideReturnType = value, _app.SystemTypes.SystemObjectType); break;
                case "signature-parameter": Assign(() => _method.Parameters[0].OverrideParameterType, value => _method.Parameters[0].OverrideParameterType = value, _app.SystemTypes.SystemObjectType); break;
                case "counter-type": Assign(() => _saved.Counter.OverrideFieldType, value => _saved.Counter.OverrideFieldType = value, _app.SystemTypes.SystemUInt32Type); break;
                case "counter-offset": Assign(() => _saved.Counter.OverrideOffset, value => _saved.Counter.OverrideOffset = value, (int?)(_saved.Counter.Offset + 4)); break;
                case "counter-readonly": Assign(() => _saved.Counter.OverrideAttributes, value => _saved.Counter.OverrideAttributes = value, (FieldAttributes?)(_saved.Counter.Attributes | FieldAttributes.InitOnly)); break;
                case "managed-alias-missing":
                    var aliases = _app.MethodsByAddress[variant.Pointer];
                    var aliasIndex = aliases.IndexOf(_method);
                    undo.Push(() => aliases.Insert(aliasIndex, _method));
                    aliases.RemoveAt(aliasIndex);
                    break;
                case "managed-alias-duplicate":
                    var duplicates = _app.MethodsByAddress[variant.Pointer];
                    undo.Push(() => duplicates.RemoveAt(duplicates.Count - 1));
                    duplicates.Add(variant.Context);
                    break;
                case "native-alias-missing":
                    var native = pe.ConcreteGenericImplementationsByAddress[variant.Pointer];
                    var nativeIndex = native.IndexOf(variant.Reference);
                    undo.Push(() => native.Insert(nativeIndex, variant.Reference));
                    native.RemoveAt(nativeIndex);
                    break;
                case "registered-pointer": Assign(() => variant.Reference.GenericVariantPtr, value => variant.Reference.GenericVariantPtr = value, variant.Pointer + 1); break;
                case "concrete-return": Assign(() => variant.Context.OverrideReturnType, value => variant.Context.OverrideReturnType = value, _app.SystemTypes.SystemInt32Type); break;
                case "interior-entry":
                    undo.Push(() => _app.MethodsByAddress.Remove(variant.Pointer + 1));
                    _app.MethodsByAddress.Add(variant.Pointer + 1, [_method]);
                    break;
                case "cached-native-body":
                    var cached = _method.RawBytes;
                    var changed = cached.AsSpan().ToArray();
                    changed[0] ^= 1;
                    Assign(() => _method.RawBytes, value => _method.RawBytes = value, new BinarySlice(changed));
                    break;
                case "native-padding":
                    var raw = (byte[])PrivateField(typeof(PE), "raw").GetValue(pe)!;
                    var offset = checked((int)pe.MapVirtualAddressToRaw(variant.NextEntry - 1, false));
                    Assign(() => raw[offset], value => raw[offset] = value, (byte)0);
                    break;
                case "native-field-offset":
                case "native-size-pointer":
                    var registration = pe.ReadReadableAtVirtualAddress<Il2CppMetadataRegistration>(_saved.Tables.Origin.MetadataRegistrationAddress);
                    var typeOrdinal = _method.DeclaringType!.Definition!.TypeIndex.Value;
                    var slot = defect == "native-size-pointer"
                        ? registration.typeDefinitionsSizes + (ulong)typeOrdinal * sizeof(ulong)
                        : pe.ReadPointerAtVirtualAddress(registration.fieldOffsetListAddress + (ulong)typeOrdinal * sizeof(ulong));
                    var bytes = (byte[])PrivateField(typeof(PE), "raw").GetValue(pe)!;
                    var positionInFile = checked((int)pe.MapVirtualAddressToRaw(slot, false));
                    Assign(() => bytes[positionInFile], value => bytes[positionInFile] = value, (byte)(bytes[positionInFile] ^ 4));
                    break;
            }
            Assert.That(X64GenericReferenceDefinitionProof.Find(_method, out _), Is.Null, defect);
            Assert.That(_saved.IsUnchanged(out _), Is.False, defect);
            var originalBody = new AsmResolver.DotNet.Code.Cil.CilMethodBody();
            var module = new ModuleDefinition("Rejected.dll");
            var destination = new MethodDefinition("Rejected", AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes.Static,
                MethodSignature.CreateStatic(module.CorLibTypeFactory.Void)) { CilMethodBody = originalBody };
            Assert.That(X64GenericReferenceDefinitionRecovery.TryGeneratePartial(_method, destination, out var reasons), Is.False, defect);
            Assert.That(destination.CilMethodBody, Is.SameAs(originalBody), defect);
            Assert.That(reasons, Is.Empty, defect);
        }
        finally
        {
            RestoreAll(undo);
        }
        Accept();
        Assert.That(_saved.IsUnchanged(out _), Is.True, "Every mutation must restore saved evidence.");
    }

    [Test]
    public void ChangedOutputIdentityOrTypedOperandsPreserveTheExistingBody()
    {
        _ = new DefinitionOnlyOutput(_method).BuildAssemblies(_app);
        var definition = _method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        var owner = _method.DeclaringType!;
        var outputOwner = owner.GetExtraData<TypeDefinition>("AsmResolverType")!;
        var field = _saved.Counter.GetExtraData<FieldDefinition>("AsmResolverField")!;
        var signature = definition.Signature!;
        var parameter = definition.GenericParameters[0];
        var originalBody = definition.CilMethodBody;
        var substituteOwner = new TypeDefinition("Synthetic", "Substitute", outputOwner.Attributes);
        foreach (var defect in new[] { "method-name", "field-name", "generic-name", "explicit-this",
                     "return-kind", "parameter-index", "generic-constraint", "method-attributes", "field-type",
                     "field-descriptor", "owner-output-binding", "coherent-owner-move" })
        {
            var undo = new Stack<Action>();
            try
            {
                void Assign<T>(Func<T> read, Action<T> write, T value)
                {
                    var previous = read();
                    undo.Push(() => write(previous));
                    write(value);
                }
                switch (defect)
                {
                    case "method-name": Assign(() => definition.Name, value => definition.Name = value, new AsmResolver.Utf8String("Substitute")); break;
                    case "field-name": Assign(() => field.Name, value => field.Name = value, new AsmResolver.Utf8String("Substitute")); break;
                    case "generic-name": Assign(() => parameter.Name, value => parameter.Name = value, new AsmResolver.Utf8String("Substitute")); break;
                    case "explicit-this": Assign(() => signature.ExplicitThis, value => signature.ExplicitThis = value, true); break;
                    case "return-kind": Assign(() => signature.ReturnType, value => signature.ReturnType = value,
                        new GenericParameterSignature(GenericParameterType.Type, 0)); break;
                    case "parameter-index": Assign(() => signature.ParameterTypes[0], value => signature.ParameterTypes[0] = value,
                        new GenericParameterSignature(GenericParameterType.Method, 1)); break;
                    case "generic-constraint": Assign(() => parameter.Attributes, value => parameter.Attributes = value,
                        AsmResolver.PE.DotNet.Metadata.Tables.GenericParameterAttributes.NonVariant); break;
                    case "method-attributes": Assign(() => definition.Attributes, value => definition.Attributes = value,
                        definition.Attributes | AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes.Static); break;
                    case "field-type": Assign(() => field.Signature, value => field.Signature = value,
                        new FieldSignature(outputOwner.DeclaringModule!.CorLibTypeFactory.Int64)); break;
                    case "field-descriptor":
                        var substituteField = new FieldDefinition(field.Name, field.Attributes, field.Signature!.FieldType);
                        undo.Push(() => outputOwner.Fields.Remove(substituteField));
                        outputOwner.Fields.Add(substituteField);
                        undo.Push(() => _saved.Counter.PutExtraData("AsmResolverField", field));
                        _saved.Counter.PutExtraData("AsmResolverField", substituteField);
                        break;
                    case "owner-output-binding":
                        undo.Push(() => owner.PutExtraData("AsmResolverType", outputOwner));
                        owner.PutExtraData("AsmResolverType", substituteOwner);
                        break;
                    case "coherent-owner-move":
                        var methodIndex = outputOwner.Methods.IndexOf(definition);
                        undo.Push(() => outputOwner.Methods.Insert(methodIndex, definition));
                        outputOwner.Methods.Remove(definition);
                        undo.Push(() => substituteOwner.Methods.Remove(definition));
                        substituteOwner.Methods.Add(definition);
                        var fieldIndex = outputOwner.Fields.IndexOf(field);
                        undo.Push(() => outputOwner.Fields.Insert(fieldIndex, field));
                        outputOwner.Fields.Remove(field);
                        undo.Push(() => substituteOwner.Fields.Remove(field));
                        substituteOwner.Fields.Add(field);
                        break;
                }
                Assert.That(X64GenericReferenceDefinitionRecovery.TryGeneratePartial(_method, definition, out var reasons), Is.False, defect);
                Assert.That(definition.CilMethodBody, Is.SameAs(originalBody), defect);
                Assert.That(reasons, Is.Empty, defect);
            }
            finally { RestoreAll(undo); }
            Assert.That(_saved.IsUnchanged(out var reason), Is.True, defect + ":" + reason);
        }
        Assert.That(X64GenericReferenceDefinitionRecovery.TryGeneratePartial(_method, definition, out _), Is.True,
            "All restored output bindings must remain eligible for incomplete typed IL.");
    }

    [Test]
    public void RealOutputPreservesTypedRecipeButStrictReportRejectsFullScope()
    {
        var output = new DefinitionOnlyOutput(_method);
        _ = output.BuildAssemblies(_app);
        var definition = _method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        var instructions = definition.CilMethodBody!.Instructions.ToArray();
        Assert.That(instructions.Select(instruction => instruction.OpCode).ToArray(), Is.EqualTo(
            new[] { CilOpCodes.Ldarg_0, CilOpCodes.Dup, CilOpCodes.Ldfld, CilOpCodes.Ldc_I4_1,
                CilOpCodes.Add, CilOpCodes.Stfld, CilOpCodes.Ldarg_1, CilOpCodes.Ret }));
        Assert.That(instructions[2].Operand, Is.SameAs(_saved.Counter.GetExtraData<FieldDefinition>("AsmResolverField")));
        Assert.That(instructions[5].Operand, Is.SameAs(instructions[2].Operand));
        var report = output.LastRecoveryReport!;
        var selected = report.Methods.Where(method => method.AssemblyName == _method.DeclaringType!.DeclaringAssembly.Name).ToArray();
        Assert.That(selected, Has.Length.EqualTo(6), "Every original managed definition remains in the report.");
        var result = selected.Single(method => method.Token == _method.Token);
        Assert.That(result.Disposition, Is.EqualTo(MethodRecoveryDisposition.Partial));
        Assert.That(result.IsUnresolved, Is.True);
        Assert.That(result.Reasons.Any(reason => reason.StartsWith("GENERIC-BODY-IMPLICIT-FAULT", StringComparison.Ordinal)), Is.True);
        Assert.That(() => report.EnsureComplete([_method.DeclaringType!.DeclaringAssembly.Name]), Throws.TypeOf<IncompleteRecoveryException>());
    }

    private X64GenericReferenceDefinitionProof.Evidence Accept()
    {
        var evidence = X64GenericReferenceDefinitionProof.Find(_method, out var reason);
        Assert.That(evidence, Is.Not.Null, reason);
        Assert.That(evidence!.IsUnchanged(out reason), Is.True, reason);
        return evidence;
    }

    // Populate the actual output declarations and use the real recovery hook for
    // the selected definition. Other methods stay NotProcessed, not successful.
    private sealed class DefinitionOnlyOutput(MethodAnalysisContext selected) : AsmResolverDllOutputFormatIlRecovery
    {
        protected override void FillMethodBody(MethodDefinition definition, MethodAnalysisContext context)
        {
            if (ReferenceEquals(context, selected)) base.FillMethodBody(definition, context);
            else if (!definition.IsAbstract) definition.CilMethodBody = new();
        }
    }

    private static T Clone<T>(T original) where T : class => (T)typeof(object)
        .GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(original, null)!;

    private static FieldInfo PrivateField(Type type, string name) =>
        type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void RestoreAll(Stack<Action> undo)
    {
        var failures = new List<Exception>();
        while (undo.TryPop(out var restore))
        {
            try { restore(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException("Output and input controls could not restore all mutations.", failures);
    }
}
