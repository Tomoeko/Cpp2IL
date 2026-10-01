using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL.BinaryStructures;
using UnityVersion = AssetRipper.Primitives.UnityVersion;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64GetterReferenceArgumentFixtureTests
{
    private MethodAnalysisContext _caller = null!;

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_NESTED_REFERENCE_GETTER_INVOCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_NESTED_REFERENCE_GETTER_INVOCATION_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _caller = app.GetAssemblyByName("NativeNestedReferenceGetterInvocationFixture")!.Types
            .Single(type => type.Name == "InvocationHolder").Methods.Single(method => method.Name == "Forward");
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void BothNullGuardsUseImplicitChecksBeforeTheEquivalentGetterRead()
    {
        var proof = Accept();
        Assert.That(proof.PayloadField.Visibility, Is.EqualTo(FieldAttributes.Family));
        var body = Definition().CilMethodBody!;
        Assert.Multiple(() =>
        {
            Assert.That(body.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Brtrue), Is.EqualTo(2));
            Assert.That(body.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld), Is.EqualTo(2));
            Assert.That(body.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Callvirt), Is.EqualTo(3));
            Assert.That(body.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Call), Is.EqualTo(1));
            Assert.That(body.Instructions.Any(instruction => instruction.OpCode == CilOpCodes.Newobj ||
                instruction.OpCode == CilOpCodes.Throw), Is.False);
            Assert.That(body.Instructions.Where(instruction => instruction.OpCode == CilOpCodes.Ldfld)
                .Select(instruction => ((IFieldDescriptor)instruction.Operand!).Name).ToArray(), Is.EqualTo(new[] { "Source", "Target" }));
        });
        var getter = body.Instructions.Single(instruction => instruction.OpCode == CilOpCodes.Call);
        Assert.That(((IMethodDescriptor)getter.Operand!).Name, Is.EqualTo("get_Payload"));
        Assert.That(body.Instructions.IndexOf(getter), Is.GreaterThan(body.Instructions.ToList()
            .FindLastIndex(instruction => instruction.OpCode == CilOpCodes.Brtrue)));
    }

    [TestCase("payload-offset")]
    [TestCase("payload-raw-data")]
    [TestCase("payload-volatile")]
    [TestCase("source-raw-data")]
    [TestCase("getter-return-data")]
    [TestCase("getter-return-byref")]
    [TestCase("getter-virtual")]
    [TestCase("getter-synchronized")]
    [TestCase("getter-private")]
    [TestCase("getter-cache")]
    [TestCase("getter-alias")]
    [TestCase("property-index")]
    [TestCase("property-token")]
    [TestCase("property-name-index")]
    [TestCase("parameter-data")]
    [TestCase("parameter-type")]
    [TestCase("parameter-token")]
    [TestCase("source-base-data")]
    [TestCase("source-base-index")]
    [TestCase("caller-cache")]
    [TestCase("callee-visibility")]
    public void OriginalAccessAndFrozenNativeFactsCannotChangeAfterAdmission(string mutation)
    {
        var proof = Accept();
        var field = mutation == "source-raw-data" ? proof.SourceField : proof.PayloadField;
        var getter = proof.Getter;
        var property = getter.DeclaringType!.Properties.Single(item => ReferenceEquals(item.Getter, getter));
        var parameter = proof.Callee.Parameters.Single();
        Action restore;
        switch (mutation)
        {
            case "payload-offset":
                var offset = field.OverrideOffset;
                field.Offset++;
                restore = () => field.OverrideOffset = offset;
                break;
            case "payload-raw-data": case "source-raw-data":
                var rawField = field.BackingData!.Field.RawFieldType!;
                var fieldData = rawField.Data;
                rawField.Data = null!;
                restore = () => rawField.Data = fieldData;
                break;
            case "payload-volatile":
                var modifiers = field.BackingData!.Field.RawFieldType!;
                var mods = modifiers.NumMods;
                modifiers.NumMods++;
                restore = () => modifiers.NumMods = mods;
                break;
            case "getter-return-data":
                var rawReturn = getter.Definition!.RawReturnType!;
                var returnData = rawReturn.Data;
                rawReturn.Data = null!;
                restore = () => rawReturn.Data = returnData;
                break;
            case "getter-return-byref":
                var descriptor = getter.Definition!.RawReturnType!;
                var byref = descriptor.Byref;
                descriptor.Byref = 1;
                restore = () => descriptor.Byref = byref;
                break;
            case "getter-virtual": case "getter-private":
                var attributes = getter.OverrideAttributes;
                getter.Attributes = mutation == "getter-virtual" ? getter.Attributes | MethodAttributes.Virtual :
                    (getter.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Private;
                restore = () => getter.OverrideAttributes = attributes;
                break;
            case "getter-synchronized":
                var implementation = getter.OverrideImplAttributes;
                getter.ImplAttributes |= MethodImplAttributes.Synchronized;
                restore = () => getter.OverrideImplAttributes = implementation;
                break;
            case "getter-cache": case "caller-cache":
                var method = mutation == "getter-cache" ? getter : _caller;
                var bytes = method.RawBytes;
                var changed = bytes.AsSpan().ToArray();
                changed[0] ^= 1;
                method.RawBytes = new BinarySlice(changed);
                restore = () => method.RawBytes = bytes;
                break;
            case "getter-alias":
                var bindings = getter.AppContext.MethodsByAddress[getter.UnderlyingPointer];
                bindings.Add(getter);
                restore = () => bindings.RemoveAt(bindings.Count - 1);
                break;
            case "property-index":
                var index = property.Definition!.get;
                property.Definition.get = property.Definition.set;
                restore = () => property.Definition.get = index;
                break;
            case "property-token":
                var token = property.Definition!.token;
                property.Definition.token++;
                restore = () => property.Definition.token = token;
                break;
            case "property-name-index":
                var name = property.Definition!.nameIndex;
                property.Definition.nameIndex++;
                restore = () => property.Definition.nameIndex = name;
                break;
            case "parameter-data":
                var rawParameter = parameter.Definition!.RawType!;
                var parameterData = rawParameter.Data;
                rawParameter.Data = null!;
                restore = () => rawParameter.Data = parameterData;
                break;
            case "parameter-type":
                var parameterType = parameter.OverrideParameterType;
                parameter.ParameterType = _caller.DeclaringType!;
                restore = () => parameter.OverrideParameterType = parameterType;
                break;
            case "parameter-token":
                var parameterToken = parameter.Definition!.token;
                parameter.Definition.token++;
                restore = () => parameter.Definition.token = parameterToken;
                break;
            case "source-base-data":
                var rawBase = proof.SourceField.FieldType.Definition!.RawBaseType!;
                var baseData = rawBase.Data;
                rawBase.Data = null!;
                restore = () => rawBase.Data = baseData;
                break;
            case "source-base-index":
                var definition = proof.SourceField.FieldType.Definition!;
                var parent = definition.ParentIndex;
                definition.ParentIndex = _caller.DeclaringType!.Definition!.ByvalTypeIndex;
                restore = () => definition.ParentIndex = parent;
                break;
            case "callee-visibility":
                var visibility = proof.Callee.OverrideAttributes;
                proof.Callee.Attributes = (proof.Callee.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Private;
                restore = () => proof.Callee.OverrideAttributes = visibility;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        try
        {
            Assert.That(X64GetterReferenceArgumentProof.TryAuthenticate(_caller, out _), Is.False);
            Assert.That(() => X64GetterReferenceArgumentRecovery.TryGenerate(_caller, Definition()), Throws.TypeOf<InvalidOperationException>());
        }
        finally { restore(); }
        Accept();
    }

    [TestCase("value-type")]
    [TestCase("bits")]
    [TestCase("datapoint")]
    [TestCase("wide-class-index")]
    [TestCase("source-self-wide")]
    [TestCase("source-self-different")]
    [TestCase("source-base-wide")]
    [TestCase("source-base-different")]
    [TestCase("object-parent")]
    [TestCase("source-initializer")]
    [TestCase("target-initializer")]
    public void FreshProofRejectsMalformedReferenceDescriptorsAndNewInitialization(string mutation)
    {
        var proof = Accept();
        var source = proof.SourceField.FieldType.Definition!;
        var raw = mutation.StartsWith("source-self", StringComparison.Ordinal) ? source.RawType :
            mutation.StartsWith("source-base", StringComparison.Ordinal) ? source.RawBaseType! :
            proof.PayloadField.BackingData!.Field.RawFieldType!;
        var bits = raw.Bits;
        var valueType = raw.ValueType;
        var datapoint = raw.Datapoint;
        var dummy = raw.Data.Dummy;
        var definition = (mutation == "target-initializer" ? proof.TargetField : proof.SourceField).FieldType.Definition!;
        var bitfield = definition.Bitfield;
        var root = _caller.AppContext.SystemTypes.SystemObjectType.Definition!;
        var parent = root.ParentIndex;
        switch (mutation)
        {
            case "value-type": raw.ValueType = 1; raw.Bits |= 1U << 31; break;
            case "bits": raw.Bits ^= 1U << 16; break;
            case "datapoint": raw.Datapoint++; break;
            case "wide-class-index": raw.Datapoint += 1UL << 32; raw.Data.Dummy += 1UL << 32; break;
            case "source-self-wide": case "source-base-wide":
                raw.Datapoint += 1UL << 32; raw.Data.Dummy += 1UL << 32; break;
            case "source-self-different": case "source-base-different":
                raw.Datapoint = raw.Data.Dummy = (ulong)_caller.DeclaringType!.Definition!.TypeIndex.Value; break;
            case "object-parent": root.ParentIndex = _caller.DeclaringType!.Definition!.ByvalTypeIndex; break;
            case "source-initializer": case "target-initializer": definition.Bitfield |= 1U << 3; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        try { Assert.That(X64GetterReferenceArgumentProof.Find(_caller), Is.Null); }
        finally
        {
            raw.Bits = bits; raw.ValueType = valueType; raw.Datapoint = datapoint; raw.Data.Dummy = dummy;
            definition.Bitfield = bitfield;
            root.ParentIndex = parent;
        }
        Accept();
    }

    private MethodDefinition Definition() => _caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!;

    private X64GetterReferenceArgumentProof.Proof Accept()
    {
        Assert.That(X64GetterReferenceArgumentProof.TryAuthenticate(_caller, out var proof), Is.True);
        Assert.That(X64GetterReferenceArgumentRecovery.TryGenerate(_caller, Definition()), Is.True);
        return proof;
    }
}

[NonParallelizable]
public class X64GetterReferenceArgumentAliasFixtureTests
{
    [TestCase("Folded", 9, 6, false)]
    [TestCase("Ambiguous", 8, 5, true)]
    public void AllOriginalManagedGetterIdentitiesPrecedeEligibility(string variant, int methods, int fields, bool ambiguous)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_NESTED_REFERENCE_GETTER_" +
            variant.ToUpperInvariant() + "_INVOCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory)) Assert.Ignore("Set the neutral exact getter alias player input for this variant.");
        Cpp2IlApi.ResetInternalState();
        try
        {
            TestGameLoader.EnsureInit();
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var assembly = app.GetAssemblyByName("NativeNestedReferenceGetter" + variant + "InvocationFixture")!;
            Assert.That(assembly.Types.Sum(type => type.Methods.Count), Is.EqualTo(methods));
            Assert.That(assembly.Types.Sum(type => type.Fields.Count), Is.EqualTo(fields));
            var owner = assembly.Types.Single(type => type.Name == "SourceOwner");
            var getter = owner.Methods.Single(method => method.Name == "get_Payload");
            var other = ambiguous ? owner.Methods.Single(method => method.Name == "get_OtherPayload") :
                assembly.Types.Single(type => type.Name == "MirrorSource").Methods.Single(method => method.Name == "get_Payload");
            Assert.That(other.UnderlyingPointer, Is.EqualTo(getter.UnderlyingPointer),
                "This exact native fixture must establish folding; synthetic alias controls do not establish it.");
            var caller = assembly.Types.Single(type => type.Name == "InvocationHolder").Methods.Single(method => method.Name == "Forward");
            Assert.That(X64GetterReferenceArgumentProof.Find(caller) == null, Is.EqualTo(ambiguous));
            if (!ambiguous)
            {
                var definition = caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                Assert.That(X64GetterReferenceArgumentRecovery.TryGenerate(caller, definition), Is.True);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
