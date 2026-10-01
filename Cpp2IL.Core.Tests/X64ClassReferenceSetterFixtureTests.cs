using System;
using System.Collections.Generic;
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
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64ClassReferenceSetterFixtureTests
{
    private MethodAnalysisContext _setter = null!;

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_CLASS_REFERENCE_SETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_CLASS_REFERENCE_SETTER_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
            Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _setter = app.GetAssemblyByName("ClassReferenceSetterFixture")!.Types
            .Single(type => type.Name == "Cell").Methods.Single(method => method.Name == "set_Current");
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void ClassDescriptorBindsOriginalPrivateStorageAndEmitsOneTypedReferenceStore()
    {
        var evidence = Accept();
        var property = _setter.DeclaringType!.Properties.Single();
        Assert.Multiple(() =>
        {
            Assert.That(evidence.Field.Name, Is.EqualTo("_current"));
            Assert.That(evidence.Field.Attributes & FieldAttributes.FieldAccessMask, Is.EqualTo(FieldAttributes.Private));
            Assert.That(evidence.Field.FieldType, Is.SameAs(_setter.Parameters[0].ParameterType));
            Assert.That(property.PropertyType, Is.SameAs(evidence.Field.FieldType));
            Assert.That(_setter.Definition!.InternalParameterData![0].RawType!.Type,
                Is.EqualTo(Il2CppTypeEnum.IL2CPP_TYPE_CLASS));
        });
        var body = _setter.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        Assert.That(X64InstanceReferenceSetterRecovery.TryGenerate(_setter, body), Is.True);
        Assert.That(body.CilMethodBody!.Instructions.Select(instruction => instruction.OpCode),
            Is.EqualTo(new[] { CilOpCodes.Ldarg_0, CilOpCodes.Ldarg_1, CilOpCodes.Stfld, CilOpCodes.Ret }));
        Assert.That(body.CilMethodBody.Instructions[2].Operand,
            Is.SameAs(evidence.Field.GetExtraData<IFieldDescriptor>("AsmResolverField")));
    }

    [TestCase("parameter-data")]
    [TestCase("parameter-index")]
    [TestCase("parameter-index-wrap")]
    [TestCase("parameter-type-index")]
    [TestCase("parameter-table-index")]
    [TestCase("parameter-table-null")]
    [TestCase("parameter-union")]
    [TestCase("parameter-bits")]
    [TestCase("parameter-modifiers")]
    [TestCase("parameter-byref")]
    [TestCase("parameter-pinned")]
    [TestCase("parameter-value-type")]
    [TestCase("parameter-kind")]
    [TestCase("parameter-override")]
    [TestCase("parameter-default")]
    [TestCase("field-data")]
    [TestCase("field-type-index")]
    [TestCase("field-class")]
    [TestCase("field-index-wrap")]
    [TestCase("field-kind")]
    [TestCase("field-offset")]
    [TestCase("field-override")]
    [TestCase("field-readonly")]
    [TestCase("property-override")]
    [TestCase("property-getter")]
    [TestCase("property-getter-index")]
    [TestCase("property-setter-index")]
    [TestCase("getter-return-index")]
    [TestCase("getter-return-data")]
    [TestCase("owner-data")]
    [TestCase("owner-type-index")]
    [TestCase("owner-base-index")]
    [TestCase("owner-base-union")]
    [TestCase("owner-base-index-wrap")]
    [TestCase("owner-method-index")]
    [TestCase("owner-field-index")]
    [TestCase("owner-property-index")]
    [TestCase("owner-base-data")]
    [TestCase("owner-base-modifiers")]
    [TestCase("owner-interface")]
    [TestCase("owner-layout")]
    [TestCase("value-data")]
    [TestCase("value-type-index")]
    [TestCase("value-base-index")]
    [TestCase("value-base-index-wrap")]
    [TestCase("value-base-data")]
    [TestCase("value-base-modifiers")]
    [TestCase("value-generic")]
    [TestCase("value-interface")]
    [TestCase("value-visibility")]
    [TestCase("value-base-override")]
    [TestCase("value-nested")]
    [TestCase("return-data")]
    [TestCase("return-value-type")]
    [TestCase("return-index")]
    [TestCase("declaring-type-index")]
    [TestCase("method-virtual")]
    [TestCase("method-synchronized")]
    [TestCase("duplicate-binding")]
    [TestCase("nonsetter-alias")]
    [TestCase("missing-field")]
    [TestCase("missing-property")]
    public void ChangedRawDeclarationsAndBindingsCannotBecomeAnOrdinaryReferenceStore(string mutation)
    {
        var evidence = Accept();
        var owner = _setter.DeclaringType!;
        var value = _setter.Parameters[0];
        var payload = value.ParameterType;
        var property = owner.Properties.Single();
        var field = evidence.Field;
        var rawParameter = value.Definition!.RawType!;
        var rawField = field.BackingData!.Field.RawFieldType!;
        var undo = new List<Action>();
        try
        {
            switch (mutation)
            {
                case "parameter-data": ClearData(rawParameter); break;
                case "parameter-index": ChangeIndex(rawParameter, ulong.MaxValue); break;
                case "parameter-index-wrap": ChangeIndex(rawParameter, rawParameter.Data.Dummy + (1UL << 32)); break;
                case "parameter-type-index":
                    var parameterTypeIndex = value.Definition!.typeIndex;
                    undo.Add(() => value.Definition.typeIndex = parameterTypeIndex);
                    value.Definition.typeIndex = Il2CppVariableWidthIndex<Il2CppType>.MakeTemporaryForFixedWidthUsage(int.MaxValue);
                    break;
                case "parameter-table-index":
                case "parameter-table-null":
                    var parameterStart = _setter.Definition!.parameterStart;
                    undo.Add(() => _setter.Definition.parameterStart = parameterStart);
                    _setter.Definition.parameterStart = mutation == "parameter-table-null"
                        ? Il2CppVariableWidthIndex<Il2CppParameterDefinition>.Null
                        : Il2CppVariableWidthIndex<Il2CppParameterDefinition>.MakeTemporaryForFixedWidthUsage(int.MaxValue);
                    break;
                case "parameter-union":
                    var dummy = rawParameter.Data.Dummy;
                    undo.Add(() => rawParameter.Data.Dummy = dummy);
                    rawParameter.Data.Dummy++;
                    break;
                case "parameter-bits":
                    var bits = rawParameter.Bits;
                    undo.Add(() => rawParameter.Bits = bits);
                    rawParameter.Bits ^= 1U << 16;
                    break;
                case "parameter-modifiers": ChangeFlags(rawParameter, 1, 0, 0, 0); break;
                case "parameter-byref": ChangeFlags(rawParameter, 0, 1, 0, 0); break;
                case "parameter-pinned": ChangeFlags(rawParameter, 0, 0, 1, 0); break;
                case "parameter-value-type": ChangeFlags(rawParameter, 0, 0, 0, 1); break;
                case "parameter-kind": ChangeKind(rawParameter, Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST); break;
                case "parameter-override":
                    undo.Add(() => value.OverrideParameterType = null);
                    value.OverrideParameterType = _setter.AppContext.SystemTypes.SystemObjectType;
                    break;
                case "parameter-default":
                    undo.Add(() => value.UseOverrideDefaultValue = false);
                    value.UseOverrideDefaultValue = true;
                    break;
                case "field-data": ClearData(rawField); break;
                case "field-type-index":
                    var fieldTypeIndex = field.BackingData!.Field.typeIndex;
                    undo.Add(() => field.BackingData.Field.typeIndex = fieldTypeIndex);
                    field.BackingData.Field.typeIndex = Il2CppVariableWidthIndex<Il2CppType>.MakeTemporaryForFixedWidthUsage(int.MaxValue);
                    break;
                case "field-class": ChangeIndex(rawField, (ulong)owner.Definition!.TypeIndex.Value); break;
                case "field-index-wrap": ChangeIndex(rawField, rawField.Data.Dummy + (1UL << 32)); break;
                case "field-kind": ChangeKind(rawField, Il2CppTypeEnum.IL2CPP_TYPE_OBJECT); break;
                case "field-offset":
                    undo.Add(() => field.OverrideOffset = null);
                    field.OverrideOffset = field.DefaultOffset + 8;
                    break;
                case "field-override":
                    undo.Add(() => field.OverrideFieldType = null);
                    field.OverrideFieldType = _setter.AppContext.SystemTypes.SystemObjectType;
                    break;
                case "field-readonly":
                    undo.Add(() => field.OverrideAttributes = null);
                    field.OverrideAttributes = field.DefaultAttributes | FieldAttributes.InitOnly;
                    break;
                case "property-override":
                    undo.Add(() => property.OverridePropertyType = null);
                    property.OverridePropertyType = _setter.AppContext.SystemTypes.SystemObjectType;
                    break;
                case "property-getter":
                    var getterIndex = property.Definition!.get;
                    undo.Add(() => property.Definition.get = getterIndex);
                    property.Definition.get = property.Definition.set;
                    break;
                case "property-getter-index":
                    var badGetter = property.Definition!.get;
                    undo.Add(() => property.Definition.get = badGetter);
                    property.Definition.get = Il2CppVariableWidthIndex<Il2CppMethodDefinition>.MakeTemporaryForFixedWidthUsage(int.MaxValue);
                    break;
                case "property-setter-index":
                    var badSetter = property.Definition!.set;
                    undo.Add(() => property.Definition.set = badSetter);
                    property.Definition.set = Il2CppVariableWidthIndex<Il2CppMethodDefinition>.MakeTemporaryForFixedWidthUsage(int.MaxValue);
                    break;
                case "getter-return-index": ChangeReturnIndex(property.Getter!); break;
                case "getter-return-data": ClearData(property.Getter!.Definition!.RawReturnType!); break;
                case "owner-data": ClearData(owner.Definition!.RawType); break;
                case "owner-type-index": ChangeByvalIndex(owner); break;
                case "owner-base-index": ChangeParentIndex(owner); break;
                case "owner-base-union":
                    var baseDummy = owner.Definition!.RawBaseType!.Data.Dummy;
                    undo.Add(() => owner.Definition.RawBaseType!.Data.Dummy = baseDummy);
                    owner.Definition.RawBaseType.Data.Dummy++;
                    break;
                case "owner-base-index-wrap":
                    ChangeIndex(owner.Definition!.RawBaseType!, owner.Definition.RawBaseType!.Data.Dummy + (1UL << 32));
                    break;
                case "owner-method-index":
                    var firstMethod = owner.Definition!.FirstMethodIdx;
                    undo.Add(() => owner.Definition.FirstMethodIdx = firstMethod);
                    owner.Definition.FirstMethodIdx = Il2CppVariableWidthIndex<Il2CppMethodDefinition>.MakeTemporaryForFixedWidthUsage(int.MaxValue);
                    break;
                case "owner-field-index":
                    var firstField = owner.Definition!.FirstFieldIdx;
                    undo.Add(() => owner.Definition.FirstFieldIdx = firstField);
                    owner.Definition.FirstFieldIdx = Il2CppVariableWidthIndex<Il2CppFieldDefinition>.MakeTemporaryForFixedWidthUsage(int.MaxValue);
                    break;
                case "owner-property-index":
                    var firstProperty = owner.Definition!.FirstPropertyId;
                    undo.Add(() => owner.Definition.FirstPropertyId = firstProperty);
                    owner.Definition.FirstPropertyId = Il2CppVariableWidthIndex<Il2CppPropertyDefinition>.MakeTemporaryForFixedWidthUsage(int.MaxValue);
                    break;
                case "owner-base-data": ClearData(owner.Definition!.RawBaseType!); break;
                case "owner-base-modifiers": ChangeFlags(owner.Definition!.RawBaseType!, 1, 0, 0, 0); break;
                case "owner-interface": ChangeAttributes(owner, owner.DefaultAttributes | TypeAttributes.Interface); break;
                case "owner-layout":
                    ChangeAttributes(owner, (owner.DefaultAttributes & ~TypeAttributes.LayoutMask) | TypeAttributes.ExplicitLayout);
                    break;
                case "value-data": ClearData(payload.Definition!.RawType); break;
                case "value-type-index": ChangeByvalIndex(payload); break;
                case "value-base-index": ChangeParentIndex(payload); break;
                case "value-base-index-wrap":
                    ChangeIndex(payload.Definition!.RawBaseType!, payload.Definition.RawBaseType!.Data.Dummy + (1UL << 32));
                    break;
                case "value-base-data": ClearData(payload.Definition!.RawBaseType!); break;
                case "value-base-modifiers": ChangeFlags(payload.Definition!.RawBaseType!, 1, 0, 0, 0); break;
                case "value-generic":
                    undo.Add(() => payload.GenericParameters.RemoveAt(payload.GenericParameters.Count - 1));
                    payload.GenericParameters.Add(null!);
                    break;
                case "value-interface": ChangeAttributes(payload, payload.DefaultAttributes | TypeAttributes.Interface); break;
                case "value-visibility":
                    ChangeAttributes(payload, payload.DefaultAttributes & ~TypeAttributes.VisibilityMask);
                    break;
                case "value-base-override":
                    undo.Add(() => payload.OverrideBaseType = null);
                    payload.OverrideBaseType = _setter.AppContext.SystemTypes.SystemStringType;
                    break;
                case "value-nested":
                    undo.Add(() => payload.DeclaringType = null);
                    payload.DeclaringType = owner;
                    break;
                case "return-data": ClearData(_setter.Definition!.RawReturnType!); break;
                case "return-value-type": ChangeFlags(_setter.Definition!.RawReturnType!, 0, 0, 0, 0); break;
                case "return-index": ChangeReturnIndex(_setter); break;
                case "declaring-type-index":
                    var declaringTypeIndex = _setter.Definition!.declaringTypeIdx;
                    undo.Add(() => _setter.Definition.declaringTypeIdx = declaringTypeIndex);
                    _setter.Definition.declaringTypeIdx = Il2CppVariableWidthIndex<Il2CppTypeDefinition>.MakeTemporaryForFixedWidthUsage(int.MaxValue);
                    break;
                case "method-virtual":
                    undo.Add(() => _setter.OverrideAttributes = null);
                    _setter.OverrideAttributes = _setter.DefaultAttributes | MethodAttributes.Virtual;
                    break;
                case "method-synchronized":
                    undo.Add(() => _setter.OverrideImplAttributes = null);
                    _setter.OverrideImplAttributes = _setter.DefaultImplAttributes | MethodImplAttributes.Synchronized;
                    break;
                case "duplicate-binding": AddAlias(_setter); break;
                case "nonsetter-alias": AddAlias(property.Getter!); break;
                case "missing-field":
                    undo.Add(() => owner.Fields.Add(field));
                    owner.Fields.Remove(field);
                    break;
                case "missing-property":
                    undo.Add(() => owner.Properties.Add(property));
                    owner.Properties.Remove(property);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            Assert.That(X64InstanceReferenceSetterProof.Find(_setter), Is.Null, mutation);
        }
        finally
        {
            for (var index = undo.Count - 1; index >= 0; index--) undo[index]();
        }
        Accept();

        void ClearData(Il2CppType raw)
        {
            var data = raw.Data;
            undo.Add(() => raw.Data = data);
            raw.Data = null!;
        }
        void ChangeIndex(Il2CppType raw, ulong index)
        {
            var pointer = raw.Datapoint;
            var data = raw.Data.Dummy;
            undo.Add(() => { raw.Datapoint = pointer; raw.Data.Dummy = data; });
            raw.Datapoint = raw.Data.Dummy = index;
        }
        void ChangeKind(Il2CppType raw, Il2CppTypeEnum kind)
        {
            var old = raw.Type;
            var bits = raw.Bits;
            undo.Add(() => { raw.Type = old; raw.Bits = bits; });
            raw.Type = kind;
            raw.Bits = (raw.Bits & ~(0xffU << 16)) | ((uint)kind << 16);
        }
        void ChangeFlags(Il2CppType raw, uint modifiers, uint byref, uint pinned, uint valueType)
        {
            var saved = (raw.Bits, raw.NumMods, raw.Byref, raw.Pinned, raw.ValueType);
            undo.Add(() => (raw.Bits, raw.NumMods, raw.Byref, raw.Pinned, raw.ValueType) = saved);
            raw.NumMods = modifiers; raw.Byref = byref; raw.Pinned = pinned; raw.ValueType = valueType;
            raw.Bits = raw.Attrs | ((uint)raw.Type << 16) | (modifiers << 24) | (byref << 29) |
                (pinned << 30) | (valueType << 31);
        }
        void ChangeAttributes(TypeAnalysisContext type, TypeAttributes attributes)
        {
            undo.Add(() => type.OverrideAttributes = null);
            type.OverrideAttributes = attributes;
        }
        void ChangeByvalIndex(TypeAnalysisContext type)
        {
            var saved = type.Definition!.ByvalTypeIndex;
            undo.Add(() => type.Definition.ByvalTypeIndex = saved);
            type.Definition.ByvalTypeIndex = Il2CppVariableWidthIndex<Il2CppType>.MakeTemporaryForFixedWidthUsage(int.MaxValue);
        }
        void ChangeParentIndex(TypeAnalysisContext type)
        {
            var saved = type.Definition!.ParentIndex;
            undo.Add(() => type.Definition.ParentIndex = saved);
            type.Definition.ParentIndex = Il2CppVariableWidthIndex<Il2CppType>.MakeTemporaryForFixedWidthUsage(int.MaxValue);
        }
        void ChangeReturnIndex(MethodAnalysisContext method)
        {
            var saved = method.Definition!.returnTypeIdx;
            undo.Add(() => method.Definition.returnTypeIdx = saved);
            method.Definition.returnTypeIdx = Il2CppVariableWidthIndex<Il2CppType>.MakeTemporaryForFixedWidthUsage(int.MaxValue);
        }
        void AddAlias(MethodAnalysisContext alias)
        {
            var aliases = _setter.AppContext.MethodsByAddress[_setter.UnderlyingPointer];
            undo.Add(() => aliases.RemoveAt(aliases.Count - 1));
            aliases.Add(alias);
        }
    }

    private X64InstanceReferenceSetterProof.Evidence Accept()
    {
        var evidence = X64InstanceReferenceSetterProof.Find(_setter);
        Assert.That(evidence, Is.Not.Null);
        return evidence!;
    }
}
