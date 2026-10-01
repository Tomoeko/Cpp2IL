using System;
using System.Buffers.Binary;
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
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64OwnerArrayBooleanEffectFixtureTests
{
    private MethodAnalysisContext _method = null!;
    private string _binary = null!;
    private string _metadata = null!;

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_OWNER_EFFECT_ARRAY_ELEMENT_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_OWNER_EFFECT_ARRAY_ELEMENT_STORE_FIXTURE_INPUT to the neutral exact player input.");
        _binary = Path.Combine(input!, "GameAssembly.dll");
        _metadata = Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(_binary, _metadata, UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _method = FindFixtureMethod();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void CheckedAccessKeepsTheOwnerStoreBeforePossibleExceptions()
    {
        var evidence = Accept();
        Assert.Multiple(() =>
        {
            Assert.That(evidence.ArrayField.Name, Is.EqualTo("Items"));
            Assert.That(evidence.OwnerEffectField!.Name, Is.EqualTo("Active"));
            Assert.That(evidence.ValueField.Name, Is.EqualTo("Enabled"));
            Assert.That(evidence.OwnerEffectValue, Is.False);
            Assert.That(evidence.Value, Is.False);
        });
        var definition = _method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        Assert.That(X64ArrayElementBooleanStoreRecovery.TryGenerate(_method, definition), Is.True);
        var body = definition.CilMethodBody!;
        var expected = evidence.CapturesArrayBeforeOwnerEffect
            ? new[] { CilOpCodes.Ldarg_0, CilOpCodes.Ldfld, CilOpCodes.Stloc, CilOpCodes.Ldarg_0,
                CilOpCodes.Ldc_I4_0, CilOpCodes.Stfld, CilOpCodes.Ldloc, CilOpCodes.Ldarg_1,
                CilOpCodes.Ldelem_Ref, CilOpCodes.Ldc_I4_0, CilOpCodes.Stfld, CilOpCodes.Ret }
            : new[] { CilOpCodes.Ldarg_0, CilOpCodes.Ldc_I4_0, CilOpCodes.Stfld, CilOpCodes.Ldarg_0,
                CilOpCodes.Ldfld, CilOpCodes.Ldarg_1, CilOpCodes.Ldelem_Ref, CilOpCodes.Ldc_I4_0,
                CilOpCodes.Stfld, CilOpCodes.Ret };
        Assert.That(body.Instructions.Select(instruction => instruction.OpCode), Is.EqualTo(expected));
        Assert.That(body.LocalVariables.Count, Is.EqualTo(evidence.CapturesArrayBeforeOwnerEffect ? 1 : 0));
        var stores = body.Instructions.Where(instruction => instruction.OpCode == CilOpCodes.Stfld).ToArray();
        Assert.That(stores.Select(instruction => instruction.Operand), Is.EqualTo(new[]
        {
            evidence.OwnerEffectField!.GetExtraData<IFieldDescriptor>("AsmResolverField"),
            evidence.ValueField.GetExtraData<IFieldDescriptor>("AsmResolverField")
        }));
    }

    public static IEnumerable<TestCaseData> RawChanges()
    {
        foreach (var role in new[] { "owner", "base", "element", "array", "owner-bool", "element-bool", "index", "return" })
        foreach (var change in new[] { "data", "union", "bits", "modifiers", "byref", "pinned", "value", "wide-union" })
            yield return new TestCaseData(role, change);
    }

    [TestCaseSource(nameof(RawChanges))]
    public void RawTypeChangesAreRejectedBeforeLazyTypeResolution(string role, string change)
    {
        var evidence = Accept();
        var raw = role switch
        {
            "owner" => _method.DeclaringType!.Definition!.RawType,
            "base" => _method.DeclaringType!.Definition!.RawBaseType!,
            "element" => evidence.ArrayField.BackingData!.Field.RawFieldType!.GetEncapsulatedType(),
            "array" => evidence.ArrayField.BackingData!.Field.RawFieldType!,
            "owner-bool" => evidence.OwnerEffectField!.BackingData!.Field.RawFieldType!,
            "element-bool" => evidence.ValueField.BackingData!.Field.RawFieldType!,
            "index" => _method.Parameters[0].Definition!.RawType!,
            "return" => _method.Definition!.RawReturnType!,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        var data = raw.Data;
        var dummy = data.Dummy;
        var datapoint = raw.Datapoint;
        var bits = raw.Bits;
        var modifiers = raw.NumMods;
        var byref = raw.Byref;
        var pinned = raw.Pinned;
        var valueType = raw.ValueType;
        try
        {
            switch (change)
            {
                case "data": raw.Data = null!; break;
                case "union": raw.Data.Dummy++; break;
                case "bits": raw.Bits ^= 1U << 16; break;
                case "modifiers": raw.NumMods = 1; break;
                case "byref": raw.Byref = 1; break;
                case "pinned": raw.Pinned = 1; break;
                case "value": raw.ValueType ^= 1; break;
                case "wide-union": raw.Data.Dummy += 1UL << 32; raw.Datapoint = raw.Data.Dummy; break;
            }
            Assert.That(X64ArrayElementBooleanStoreProof.Find(_method), Is.Null, role + ": " + change);
        }
        finally
        {
            raw.Data = data;
            raw.Data.Dummy = dummy;
            raw.Datapoint = datapoint;
            raw.Bits = bits;
            raw.NumMods = modifiers;
            raw.Byref = byref;
            raw.Pinned = pinned;
            raw.ValueType = valueType;
        }
        Accept();
    }

    [TestCase("owner-offset")]
    [TestCase("array-offset")]
    [TestCase("element-offset")]
    [TestCase("owner-type")]
    [TestCase("element-type")]
    [TestCase("owner-readonly")]
    [TestCase("element-private")]
    [TestCase("neighbor-overlap")]
    [TestCase("owner-base")]
    [TestCase("element-base")]
    [TestCase("owner-cctor")]
    [TestCase("element-cctor")]
    [TestCase("synchronized")]
    [TestCase("duplicate-binding")]
    [TestCase("extra-binding")]
    [TestCase("missing-method")]
    [TestCase("sibling-cctor")]
    [TestCase("sibling-flags")]
    [TestCase("owner-raw-constructor-static")]
    [TestCase("element-raw-constructor-static")]
    [TestCase("owner-raw-constructor-virtual")]
    [TestCase("owner-raw-constructor-abstract")]
    [TestCase("owner-raw-constructor-synchronized")]
    [TestCase("owner-raw-constructor-return")]
    public void MetadataAndLayoutChangesCannotHideOrReorderTheEffect(string change)
    {
        var evidence = Accept();
        var owner = _method.DeclaringType!;
        var element = evidence.ValueField.DeclaringType;
        var undo = new List<Action>();
        try
        {
            switch (change)
            {
                case "owner-offset": Offset(evidence.OwnerEffectField!); break;
                case "array-offset": Offset(evidence.ArrayField); break;
                case "element-offset": Offset(evidence.ValueField); break;
                case "owner-type": Type(evidence.OwnerEffectField!); break;
                case "element-type": Type(evidence.ValueField); break;
                case "owner-readonly":
                    undo.Add(() => evidence.OwnerEffectField!.OverrideAttributes = null);
                    evidence.OwnerEffectField!.OverrideAttributes = evidence.OwnerEffectField.DefaultAttributes | FieldAttributes.InitOnly;
                    break;
                case "element-private":
                    undo.Add(() => evidence.ValueField.OverrideAttributes = null);
                    evidence.ValueField.OverrideAttributes = (evidence.ValueField.DefaultAttributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Private;
                    break;
                case "neighbor-overlap":
                    var neighbor = owner.Fields.First(field => !field.IsStatic && !ReferenceEquals(field, evidence.OwnerEffectField) &&
                        !ReferenceEquals(field, evidence.ArrayField));
                    undo.Add(() => neighbor.OverrideOffset = null);
                    neighbor.OverrideOffset = evidence.OwnerEffectField!.Offset;
                    break;
                case "owner-base": Base(owner); break;
                case "element-base": Base(element); break;
                case "owner-cctor": Cctor(owner); break;
                case "element-cctor": Cctor(element); break;
                case "synchronized":
                    undo.Add(() => _method.OverrideImplAttributes = null);
                    _method.OverrideImplAttributes = _method.DefaultImplAttributes | MethodImplAttributes.Synchronized;
                    break;
                case "duplicate-binding": Alias(_method); break;
                case "extra-binding": Alias(owner.Methods.First(method => method.Name == ".ctor")); break;
                case "missing-method":
                    var index = owner.Methods.IndexOf(_method);
                    undo.Add(() => owner.Methods.Insert(index, _method));
                    owner.Methods.RemoveAt(index);
                    break;
                case "sibling-cctor":
                    var constructor = owner.Methods.First(method => method.Name == ".ctor");
                    undo.Add(() => constructor.OverrideName = null);
                    constructor.OverrideName = ".cctor";
                    break;
                case "sibling-flags":
                    var sibling = owner.Methods.First(method => method.Name == ".ctor");
                    undo.Add(() => sibling.OverrideAttributes = null);
                    sibling.OverrideAttributes = sibling.DefaultAttributes | MethodAttributes.Static;
                    break;
                case "owner-raw-constructor-static": ConstructorFlags(owner, MethodAttributes.Static); break;
                case "element-raw-constructor-static": ConstructorFlags(element, MethodAttributes.Static); break;
                case "owner-raw-constructor-virtual": ConstructorFlags(owner, MethodAttributes.Virtual); break;
                case "owner-raw-constructor-abstract": ConstructorFlags(owner, MethodAttributes.Abstract); break;
                case "owner-raw-constructor-synchronized":
                    var rawConstructor = owner.Methods.First(method => method.Name == ".ctor").Definition!;
                    var impl = rawConstructor.iflags;
                    undo.Add(() => rawConstructor.iflags = impl);
                    rawConstructor.iflags |= (ushort)MethodImplAttributes.Synchronized;
                    break;
                case "owner-raw-constructor-return":
                    var constructorReturn = owner.Methods.First(method => method.Name == ".ctor").Definition!;
                    var returnIndex = constructorReturn.returnTypeIdx;
                    undo.Add(() => constructorReturn.returnTypeIdx = returnIndex);
                    constructorReturn.returnTypeIdx = _method.Parameters[0].Definition!.typeIndex;
                    break;
            }
            Assert.That(X64ArrayElementBooleanStoreProof.Find(_method), Is.Null, change);
        }
        finally { for (var index = undo.Count - 1; index >= 0; index--) undo[index](); }
        Accept();

        void Offset(FieldAnalysisContext field)
        {
            undo.Add(() => field.OverrideOffset = null);
            field.OverrideOffset = field.DefaultOffset + 1;
        }
        void Type(FieldAnalysisContext field)
        {
            undo.Add(() => field.OverrideFieldType = null);
            field.OverrideFieldType = _method.AppContext.SystemTypes.SystemByteType;
        }
        void Base(TypeAnalysisContext type)
        {
            undo.Add(() => type.OverrideBaseType = null);
            type.OverrideBaseType = _method.AppContext.SystemTypes.SystemStringType;
        }
        void Cctor(TypeAnalysisContext type)
        {
            var bits = type.Definition!.Bitfield;
            undo.Add(() => type.Definition.Bitfield = bits);
            type.Definition.Bitfield |= 1U << 3;
        }
        void Alias(MethodAnalysisContext method)
        {
            var aliases = _method.AppContext.MethodsByAddress[_method.UnderlyingPointer];
            undo.Add(() => aliases.RemoveAt(aliases.Count - 1));
            aliases.Add(method);
        }
        void ConstructorFlags(TypeAnalysisContext type, MethodAttributes change)
        {
            var constructor = type.Methods.First(method => method.Name == ".ctor").Definition!;
            var flags = constructor.flags;
            undo.Add(() => constructor.flags = flags);
            constructor.flags |= (ushort)change;
        }
    }

    [Test]
    public void RemovingOnlyTheOwnerEffectRetainsTheExistingElementStoreRoute()
    {
        var evidence = Accept();
        var body = X64Stack28BodyProof.Read(_method, 17, 128)!;
        var pe = (PE)_method.AppContext.Binary;
        var effect = body[evidence.CapturesArrayBeforeOwnerEffect ? 2 : 1];
        var effectRaw = checked((int)pe.MapVirtualAddressToRaw(effect.IP, false));
        var bodyEndRaw = checked((int)pe.MapVirtualAddressToRaw(body[^1].NextIP - 1, false) + 1);
        var original = File.ReadAllBytes(_binary);
        var changed = (byte[])original.Clone();
        Array.Copy(changed, effectRaw + effect.Length, changed, effectRaw,
            bodyEndRaw - effectRaw - effect.Length);
        Array.Fill(changed, (byte)0xCC, bodyEndRaw - effect.Length, effect.Length);
        foreach (var call in new[] { body[14], body[16] })
        {
            var operand = checked((int)pe.MapVirtualAddressToRaw(call.IP, false) - effect.Length + 1);
            var displacement = BinaryPrimitives.ReadInt32LittleEndian(changed.AsSpan(operand, 4));
            BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(operand, 4), checked(displacement + effect.Length));
        }
        var metadata = File.ReadAllBytes(_metadata);
        try
        {
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(changed, metadata, UnityVersion.Parse("2021.3.35f1"));
            var method = FindFixtureMethod();
            var plain = X64ArrayElementBooleanStoreProof.Find(method);
            Assert.Multiple(() =>
            {
                Assert.That(X64Stack28BodyProof.Read(method, 16, 96), Is.Not.Null);
                Assert.That(plain, Is.Not.Null);
                Assert.That(plain?.OwnerEffectField, Is.Null);
                Assert.That(plain?.ArrayField.Name, Is.EqualTo("Items"));
                Assert.That(plain?.ValueField.Name, Is.EqualTo("Enabled"));
                Assert.That(plain?.Value, Is.False);
            });
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(original, metadata, UnityVersion.Parse("2021.3.35f1"));
            _method = FindFixtureMethod();
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(_method.AppContext);
        }
        Accept();
    }

    [Test]
    public void FileBackedGuardsHelpersAndPaddingRemainRequired()
    {
        Accept();
        var app = _method.AppContext;
        var body = X64Stack28BodyProof.Read(_method, 17, 128)!;
        var region = X64UnwindProof.ForApplication(app)!.ClassifySpan(_method.UnderlyingPointer, _method.UnderlyingPointer + 1);
        var addresses = new List<ulong> { body[4].NextIP - 1, body[6].IP, body[10].NextIP - 1,
            body[14].NextIP - 1, body[16].NextIP - 1 };
        if (body[^1].NextIP < region.End) addresses.Add(body[^1].NextIP);
        var pe = (PE)app.Binary;
        var offsets = addresses.Select(address => checked((int)pe.MapVirtualAddressToRaw(address, false))).ToArray();
        var binary = File.ReadAllBytes(_binary);
        var metadata = File.ReadAllBytes(_metadata);
        try
        {
            foreach (var offset in offsets)
            {
                var changed = (byte[])binary.Clone();
                changed[offset] ^= 2;
                Cpp2IlApi.ResetInternalState();
                Cpp2IlApi.InitializeLibCpp2Il(changed, metadata, UnityVersion.Parse("2021.3.35f1"));
                Assert.That(X64ArrayElementBooleanStoreProof.Find(FindFixtureMethod()), Is.Null);
            }
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            _method = FindFixtureMethod();
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(_method.AppContext);
        }
        Accept();
    }

    private X64ArrayElementBooleanStoreProof.Evidence Accept()
    {
        var evidence = X64ArrayElementBooleanStoreProof.Find(_method);
        Assert.That(evidence?.OwnerEffectField, Is.Not.Null);
        return evidence!;
    }

    private static MethodAnalysisContext FindFixtureMethod() => Cpp2IlApi.CurrentAppContext!
        .GetAssemblyByName("OwnerEffectArrayElementStoreFixture")!.Types.Single(type => type.Name == "Catalog")
        .Methods.Single(method => method.Name == "Clear");
}
