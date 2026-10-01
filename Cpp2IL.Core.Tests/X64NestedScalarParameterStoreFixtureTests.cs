using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
[TestFixture("CPP2IL_NATIVE_NESTED_SCALAR_PARAMETER_STORE_FIXTURE_INPUT", "NativeNestedScalarParameterStoreFixture", 4)]
[TestFixture("CPP2IL_NATIVE_NESTED_OWNER_SCALAR_PARAMETER_STORE_FIXTURE_INPUT", "NativeNestedOwnerScalarParameterStoreFixture", 1)]
public class X64NestedScalarParameterStoreFixtureTests(string variable, string assemblyName, int stores)
{
    private MethodAnalysisContext[] _methods = null!;
    private MethodAnalysisContext _single = null!;

    [OneTimeSetUp]
    public void LoadOriginalPlayer()
    {
        var input = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(input)) Assert.Ignore("Set " + variable + " to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
            Directory.EnumerateFiles(input!, "global-metadata.dat", SearchOption.AllDirectories).Single(),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _methods = app.GetAssemblyByName(assemblyName)!.Types.Single(type => type.Name == "Holder")
            .Methods.Where(method => method.Name != ".ctor").ToArray();
        _single = _methods.Single(method => method.Name == "StoreSingle");
        Assert.That(_methods, Has.Length.EqualTo(stores));
        AcceptAll();
    }

    [OneTimeTearDown]
    public void ReleasePlayer() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void EveryOriginalStoreEmitsTheCapturedReferenceAndDeclaredArgument()
    {
        foreach (var method in _methods)
        {
            var proof = X64NestedScalarParameterStoreProof.Find(method)!;
            var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            Assert.That(X64NestedScalarParameterStoreRecovery.TryGeneratePartial(method, definition, out var unresolved), Is.True);
            Assert.That(unresolved, Has.Length.EqualTo(2));
            Assert.That(unresolved.Any(reason => reason.Contains("implicit owner-null", StringComparison.Ordinal)), Is.True);
            Assert.That(unresolved.Any(reason => reason.Contains("volatile", StringComparison.Ordinal)), Is.True);
            Assert.That(definition.CilMethodBody!.Instructions.Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { CilOpCodes.Ldarg_0, CilOpCodes.Ldfld, CilOpCodes.Ldarg_1, CilOpCodes.Stfld, CilOpCodes.Ret }));
            Assert.That(definition.CilMethodBody.Instructions[1].Operand,
                Is.SameAs(proof.SourceField.GetExtraData<IFieldDescriptor>("AsmResolverField")));
            Assert.That(definition.CilMethodBody.Instructions[3].Operand,
                Is.SameAs(proof.ValueField.GetExtraData<IFieldDescriptor>("AsmResolverField")));
        }
    }

    [TestCase("source")]
    [TestCase("value")]
    public void EqualSignatureOutputFieldsCannotReplaceTheOriginalOrderedMember(string role)
    {
        var proof = X64NestedScalarParameterStoreProof.Find(_single)!;
        var context = role == "source" ? proof.SourceField : proof.ValueField;
        var original = context.GetExtraData<FieldDefinition>("AsmResolverField")!;
        var owner = original.DeclaringType!;
        var replacement = new FieldDefinition(original.Name, original.Attributes, original.Signature);
        var destination = _single.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        var previous = destination.CilMethodBody;
        var sentinel = new CilMethodBody();
        owner.Fields.Add(replacement);
        context.PutExtraData("AsmResolverField", replacement);
        destination.CilMethodBody = sentinel;
        try
        {
            Assert.That(X64NestedScalarParameterStoreRecovery.TryGeneratePartial(_single, destination, out var reasons), Is.False);
            Assert.That(destination.CilMethodBody, Is.SameAs(sentinel));
            Assert.That(reasons, Is.Empty);
        }
        finally
        {
            context.PutExtraData("AsmResolverField", original);
            owner.Fields.Remove(replacement);
            destination.CilMethodBody = previous;
        }
        AcceptAll();
    }

    [TestCase("name")]
    [TestCase("explicit-this")]
    public void ChangedOutputMethodIdentityOrCallingConventionKeepsThePreviousBody(string defect)
    {
        var destination = _single.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        var name = destination.Name;
        var explicitThis = destination.Signature!.ExplicitThis;
        var previous = destination.CilMethodBody;
        var sentinel = new CilMethodBody();
        destination.CilMethodBody = sentinel;
        try
        {
            if (defect == "name") destination.Name = "Changed";
            else destination.Signature.ExplicitThis = true;
            Assert.That(X64NestedScalarParameterStoreRecovery.TryGeneratePartial(_single, destination, out var reasons), Is.False);
            Assert.That(destination.CilMethodBody, Is.SameAs(sentinel));
            Assert.That(reasons, Is.Empty);
        }
        finally
        {
            destination.Name = name; destination.Signature.ExplicitThis = explicitThis; destination.CilMethodBody = previous;
        }
        AcceptAll();
    }

    [TestCase("static")]
    [TestCase("virtual")]
    [TestCase("synchronized")]
    [TestCase("return-index")]
    [TestCase("parameter-name")]
    [TestCase("parameter-ref")]
    [TestCase("parameter-kind")]
    [TestCase("source-private-type")]
    [TestCase("value-private")]
    [TestCase("value-readonly")]
    [TestCase("value-offset")]
    [TestCase("owner-initializer")]
    [TestCase("target-initializer")]
    [TestCase("sibling-constructor")]
    [TestCase("alias-duplicate")]
    [TestCase("alias-missing")]
    [TestCase("cached-native")]
    [TestCase("caller-pointer-slot")]
    [TestCase("source-reflection-attrs")]
    [TestCase("source-index-cache")]
    [TestCase("neighbor-layout-cache")]
    public void OriginalAbiFieldsLifecycleAliasesAndNativeInputsRemainMandatory(string defect)
    {
        var saved = X64NestedScalarParameterStoreProof.Find(_single)!;
        var owner = _single.DeclaringType!;
        var target = saved.ValueField.DeclaringType;
        var undo = new Stack<Action>();
        void Assign<T>(Func<T> read, Action<T> write, T changed)
        {
            var value = read(); undo.Push(() => write(value)); write(changed);
        }
        try
        {
            var definition = _single.Definition!;
            var parameter = _single.Parameters[0];
            var rawParameter = parameter.Definition!.RawType ?? throw new InvalidOperationException("Original parameter descriptor is absent.");
            switch (defect)
            {
                case "static": Assign(() => definition.flags, value => definition.flags = value, (ushort)(definition.flags | (ushort)MethodAttributes.Static)); break;
                case "virtual": Assign(() => definition.flags, value => definition.flags = value, (ushort)(definition.flags | (ushort)MethodAttributes.Virtual)); break;
                case "synchronized": Assign(() => definition.iflags, value => definition.iflags = value, (ushort)(definition.iflags | (ushort)MethodImplAttributes.Synchronized)); break;
                case "return-index": Assign(() => definition.returnTypeIdx, value => definition.returnTypeIdx = value, _single.AppContext.SystemTypes.SystemInt32Type.Definition!.ByvalTypeIndex); break;
                case "parameter-name": Assign(() => parameter.Name, value => parameter.Name = value, parameter.Name + "Changed"); break;
                case "parameter-ref": Assign(() => rawParameter.Byref, value => rawParameter.Byref = value, 1u); Assign(() => rawParameter.Bits, value => rawParameter.Bits = value, rawParameter.Bits | (1u << 29)); break;
                case "parameter-kind": Assign(() => rawParameter.Type, value => rawParameter.Type = value, Il2CppTypeEnum.IL2CPP_TYPE_I4); Assign(() => rawParameter.Bits, value => rawParameter.Bits = value, (rawParameter.Bits & ~(0xffu << 16)) | ((uint)Il2CppTypeEnum.IL2CPP_TYPE_I4 << 16)); break;
                case "source-private-type": Assign(() => target.Attributes, value => target.Attributes = value, (target.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NotPublic); break;
                case "value-private": Assign(() => saved.ValueField.Attributes, value => saved.ValueField.Attributes = value, (saved.ValueField.Attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Private); break;
                case "value-readonly": Assign(() => saved.ValueField.Attributes, value => saved.ValueField.Attributes = value, saved.ValueField.Attributes | FieldAttributes.InitOnly); break;
                case "value-offset": Assign(() => saved.ValueField.Offset, value => saved.ValueField.Offset = value, saved.ValueField.Offset + 1); break;
                case "owner-initializer": Assign(() => owner.Definition!.Bitfield, value => owner.Definition!.Bitfield = value, owner.Definition!.Bitfield | (1u << 3)); break;
                case "target-initializer": Assign(() => target.Definition!.Bitfield, value => target.Definition!.Bitfield = value, target.Definition!.Bitfield | (1u << 3)); break;
                case "sibling-constructor":
                    var constructor = owner.Methods.Single(method => method.Name == ".ctor");
                    Assign(() => constructor.Name, value => constructor.Name = value, ".cctor"); break;
                case "alias-duplicate":
                    var aliases = _single.AppContext.MethodsByAddress[_single.UnderlyingPointer];
                    aliases.Add(_single); undo.Push(() => aliases.RemoveAt(aliases.Count - 1)); break;
                case "alias-missing":
                    var originalAliases = _single.AppContext.MethodsByAddress[_single.UnderlyingPointer];
                    Assign(() => _single.AppContext.MethodsByAddress[_single.UnderlyingPointer], value => _single.AppContext.MethodsByAddress[_single.UnderlyingPointer] = value, new List<MethodAnalysisContext>(originalAliases.Where(method => !ReferenceEquals(method, _single)))); break;
                case "cached-native":
                    var changed = _single.RawBytes.AsSpan().ToArray(); changed[0] ^= 1;
                    Assign(() => _single.RawBytes, value => _single.RawBytes = value, new BinarySlice(changed)); break;
                case "caller-pointer-slot":
                    var module = owner.DeclaringAssembly.CodeGenModule!;
                    var pointers = _single.AppContext.Binary.GetCodegenModuleMethodPointers(_single.AppContext.Binary.GetCodegenModuleIndex(module));
                    var ordinal = checked((int)((definition.token & 0x00ffffff) - 1));
                    Assign(() => pointers[ordinal], value => pointers[ordinal] = value, pointers[ordinal] + 1); break;
                case "source-reflection-attrs":
                    var data = saved.SourceField.BackingData!;
                    Assign(() => data.Attributes, value => data.Attributes = value, data.Attributes | FieldAttributes.InitOnly); break;
                case "source-index-cache":
                    var sourceData = saved.SourceField.BackingData!;
                    Assign(() => sourceData.IndexInParent, value => sourceData.IndexInParent = value, sourceData.IndexInParent + 1); break;
                case "neighbor-layout-cache":
                    var neighbor = target.Fields.Single(field => field.Name == "Before").BackingData!;
                    Assign(() => neighbor.FieldOffset, value => neighbor.FieldOffset = value, neighbor.FieldOffset + 1); break;
            }
            var current = X64NestedScalarParameterStoreProof.Find(_single);
            Assert.That(current, Is.Null, defect);
        }
        finally
        {
            List<Exception>? failures = null;
            while (undo.TryPop(out var restore))
                try { restore(); } catch (Exception failure) { (failures ??= []).Add(failure); }
            if (failures != null) throw new AggregateException(failures);
        }
        Assert.That(saved.Matches(X64NestedScalarParameterStoreProof.Find(_single)!), Is.True);
        AcceptAll();
    }

    [TestCase("null-owner-descriptor")]
    [TestCase("wide-source-class")]
    [TestCase("source-union")]
    [TestCase("value-modifier")]
    public void RetainedRawDescriptorsCannotNormalizeMalformedCachedData(string defect)
    {
        var proof = X64NestedScalarParameterStoreProof.Find(_single)!;
        var raw = defect == "null-owner-descriptor" ? _single.DeclaringType!.Definition!.RawType :
            defect == "value-modifier" ? proof.ValueField.BackingData!.Field.RawFieldType! : proof.SourceField.BackingData!.Field.RawFieldType!;
        var data = raw.Data ?? throw new InvalidOperationException("Original descriptor union is absent.");
        var dummy = data.Dummy; var point = raw.Datapoint; var bits = raw.Bits; var modifiers = raw.NumMods;
        try
        {
            switch (defect)
            {
                case "null-owner-descriptor": raw.Data = null!; break;
                case "wide-source-class": data.Dummy += 1ul << 32; raw.Datapoint = data.Dummy; break;
                case "source-union": data.Dummy++; break;
                case "value-modifier": raw.NumMods = 1; raw.Bits |= 1u << 24; break;
            }
            Assert.That(X64NestedScalarParameterStoreProof.Find(_single), Is.Null, defect);
        }
        finally { raw.Data = data; data.Dummy = dummy; raw.Datapoint = point; raw.Bits = bits; raw.NumMods = modifiers; }
        AcceptAll();
    }

    [Test]
    public void OriginalNestedOwnershipCannotBeReplacedByAContextOnlyLink()
    {
        var owner = _single.DeclaringType!;
        var enclosing = owner.DeclaringType;
        if (enclosing == null)
        {
            Assert.Ignore("This control requires the separately scoped nested fixture.");
            return;
        }
        var parent = owner.DeclaringType;
        var members = enclosing.NestedTypes.ToArray();
        var index = owner.Definition!.DeclaringTypeIndex;
        var section = _single.AppContext.Metadata.metadataHeader.nestedTypes;
        var sectionOffset = section.Offset;
        try
        {
            enclosing.NestedTypes.Clear();
            Assert.That(X64NestedScalarParameterStoreProof.Find(_single), Is.Null);
            enclosing.NestedTypes.AddRange(members);
            owner.DeclaringType = null;
            Assert.That(X64NestedScalarParameterStoreProof.Find(_single), Is.Null);
            owner.DeclaringType = parent;
            owner.Definition.DeclaringTypeIndex = _single.AppContext.SystemTypes.SystemObjectType.Definition!.ByvalTypeIndex;
            Assert.That(X64NestedScalarParameterStoreProof.Find(_single), Is.Null);
            owner.Definition.DeclaringTypeIndex = index;
            section.Offset++;
            Assert.That(X64NestedScalarParameterStoreProof.Find(_single), Is.Null);
        }
        finally
        {
            owner.DeclaringType = parent; owner.Definition.DeclaringTypeIndex = index;
            enclosing.NestedTypes.Clear(); enclosing.NestedTypes.AddRange(members); section.Offset = sectionOffset;
        }
        AcceptAll();
    }

    private void AcceptAll()
    {
        foreach (var method in _methods)
            Assert.That(X64NestedScalarParameterStoreProof.Find(method), Is.Not.Null, method.Name);
    }
}
