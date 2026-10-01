using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Utf8String = AsmResolver.Utf8String;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.SourceEmission;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[TestFixture(false)]
[TestFixture(true)]
[NonParallelizable]
public class X64ScalarStaticConstructorFixtureTests
{
    private readonly bool _synthetic;
    public X64ScalarStaticConstructorFixtureTests(bool synthetic) => _synthetic = synthetic;
    private ApplicationAnalysisContext _app = null!;
    private MethodAnalysisContext[] _methods = null!;

    [OneTimeSetUp]
    public void LoadOriginalPlayer()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_SCALAR_DOUBLE_LEAF_FIXTURE_INPUT");
        var binary = _synthetic && !string.IsNullOrEmpty(input) ? Path.Combine(input, "GameAssembly.dll") :
            _synthetic ? null : Environment.GetEnvironmentVariable("CPP2IL_SCALAR_DOUBLE_LEAF_BINARY");
        var metadata = _synthetic && !string.IsNullOrEmpty(input) ?
            Path.Combine(input, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat") :
            _synthetic ? null : Environment.GetEnvironmentVariable("CPP2IL_SCALAR_DOUBLE_LEAF_METADATA");
        if (string.IsNullOrEmpty(binary) || string.IsNullOrEmpty(metadata))
            Assert.Ignore("Configure an original exact-target player for the static initializer controls.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(binary!, metadata!, UnityVersion.Parse("2021.3.35f1"));
        _app = Cpp2IlApi.CurrentAppContext!;
        Assert.That(((PE)_app.Binary).BaseStream, Is.InstanceOf<MemoryStream>(), "Native controls must not change the original input files.");
        _methods = _app.Assemblies.Where(assembly => _synthetic ? assembly.Name == "NativeScalarDoubleLeafFixture" :
                UnityTargetAssemblyScope.Classify(assembly) == UnityTargetAssemblyKind.Application)
            .SelectMany(assembly => assembly.Types).SelectMany(type => type.Methods)
            .Where(method => method.Name == ".cctor" && X64ScalarStaticConstructorProof.Find(method) != null).ToArray();
        Assert.That(_methods, Is.Not.Empty);
        if (_synthetic)
        {
            var types = _app.Assemblies.Single(assembly => assembly.Name == "NativeScalarDoubleLeafFixture").Types
                .Where(type => type.Name != "<Module>").ToArray();
            Assert.That(types.Length, Is.EqualTo(2));
            Assert.That(types.Sum(type => type.Methods.Count), Is.EqualTo(7));
            Assert.That(types.Sum(type => type.Fields.Count), Is.EqualTo(4));
            Assert.That(_methods.Length, Is.EqualTo(2), "Both original initializer bodies remain in the complete fixture scope.");
            Assert.That(_methods.Select(method => X64ScalarStaticConstructorProof.Find(method)!.ValueBits),
                Is.EquivalentTo(new uint[] { 17, 29 }));
        }
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(_app);
        foreach (var method in _methods) Assert.That(X64ScalarStaticConstructorProof.TryAuthenticate(method, out _), Is.True);
    }

    [OneTimeTearDown]
    public void ReleasePlayer() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void CompleteInitializerPublishesOnlyTheAuthenticatedSignedWordStore()
    {
        foreach (var method in _methods)
        {
            var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            var proof = method.GetExtraData<X64ScalarStaticConstructorProof.Evidence>(X64ScalarStaticConstructorProof.EvidenceKey)!;
            Assert.That(proof.IsUnchanged(), Is.True);
            Assert.That(X64ScalarStaticConstructorRecovery.TryGenerate(method, output), Is.True);
            var instructions = output.CilMethodBody!.Instructions;
            Assert.That(instructions.Select(site => site.OpCode), Is.EqualTo(new[] { CilOpCodes.Ldc_I4, CilOpCodes.Stsfld, CilOpCodes.Ret }));
            Assert.That(instructions[0].Operand, Is.EqualTo(unchecked((int)proof.ValueBits)));
            Assert.That(instructions[1].Operand, Is.SameAs(proof.StaticField.GetExtraData<FieldDefinition>("AsmResolverField")));
            Assert.That(output.Attributes, Is.EqualTo((AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes)method.Attributes));
        }
    }

    [TestCase("static-offset")]
    [TestCase("static-type")]
    [TestCase("native-body")]
    [TestCase("cached-body")]
    [TestCase("helper")]
    [TestCase("usage")]
    [TestCase("static-size")]
    [TestCase("evidence-removed")]
    [TestCase("registered-alias")]
    public void ChangedOriginalInputsOrLostSavedProofCannotReplaceAValidBody(string defect)
    {
        var method = _methods[0];
        var proof = method.GetExtraData<X64ScalarStaticConstructorProof.Evidence>(X64ScalarStaticConstructorProof.EvidenceKey)!;
        var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        Assert.That(X64ScalarStaticConstructorRecovery.TryGenerate(method, output), Is.True);
        var previous = output.CilMethodBody;
        var shape = X64ScalarWrapperStaticConstructorProof.TryProveShape(X64Stack28BodyProof.Read(method, 11, 96)!)!.Value;
        var undo = new Stack<Action>();
        try
        {
            switch (defect)
            {
                case "static-offset": Change<int?>(undo, () => proof.StaticField.OverrideOffset, value => proof.StaticField.OverrideOffset = value, 4); break;
                case "static-type": Change(undo, () => proof.StaticField.OverrideFieldType, value => proof.StaticField.OverrideFieldType = value, _app.SystemTypes.SystemUInt32Type); break;
                case "native-body": MutateNative(undo, method.UnderlyingPointer, 1); break;
                case "helper": MutateNative(undo, shape.Initializer, 1); break;
                case "usage": MutateNative(undo, shape.TypeInfoSlot, 2); break;
                case "static-size": MutateNative(undo, _app.Binary.TypeDefinitionSizePointers[method.DeclaringType!.Definition!.TypeIndex.Value] + 8, 1); break;
                case "cached-body":
                    var changed = method.RawBytes.AsSpan().ToArray(); changed[0] ^= 1;
                    Change(undo, () => method.RawBytes, value => method.RawBytes = value, new BinarySlice(changed)); break;
                case "evidence-removed": Change(undo, () => method.GetExtraData<X64ScalarStaticConstructorProof.Evidence>(X64ScalarStaticConstructorProof.EvidenceKey),
                    value => method.PutExtraData(X64ScalarStaticConstructorProof.EvidenceKey, value!), null); break;
                case "registered-alias":
                    Assert.That(_app.Binary.TryGetGenericMethodTableRegistration(out var registration), Is.True);
                    Assert.That(registration.MethodPointerCount, Is.GreaterThan(0));
                    WriteNative(undo, registration.MethodPointersAddress, BitConverter.GetBytes(method.UnderlyingPointer)); break;
            }
            if (defect != "evidence-removed")
                Assert.That(X64ScalarStaticConstructorProof.Find(method), Is.Null,
                    "This input mutation must reject fresh qualification, independently of saved evidence.");
            Assert.That(X64ScalarStaticConstructorRecovery.TryGenerate(method, output), Is.False);
            Assert.That(output.CilMethodBody, Is.SameAs(previous));
        }
        finally { Restore(undo); }
        Assert.That(proof.IsUnchanged(), Is.True);
        Assert.That(X64ScalarStaticConstructorRecovery.TryGenerate(method, output), Is.True);
    }

    [Test]
    public void CoherentUnusedPrimitiveDataChangeIsFreshlyEligibleButInvalidatesSavedEvidence()
    {
        var method = _methods[0];
        var proof = method.GetExtraData<X64ScalarStaticConstructorProof.Evidence>(X64ScalarStaticConstructorProof.EvidenceKey)!;
        var raw = proof.StaticField.BackingData!.Field.RawFieldType!;
        Assert.That(_app.Binary.TryGetTypeVirtualAddress(raw, out var address), Is.True);
        var data = raw.Datapoint; var union = raw.Data.Dummy;
        var undo = new Stack<Action>();
        undo.Push(() => { raw.Datapoint = data; raw.Data.Dummy = union; });
        try
        {
            raw.Datapoint ^= 1; raw.Data.Dummy = raw.Datapoint;
            WriteNative(undo, address, BitConverter.GetBytes(raw.Datapoint));
            Assert.That(X64ScalarStaticConstructorProof.Find(method), Is.Not.Null);
            Assert.That(proof.IsUnchanged(), Is.False);
            var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            var previous = output.CilMethodBody;
            Assert.That(X64ScalarStaticConstructorRecovery.TryGenerate(method, output), Is.False);
            Assert.That(output.CilMethodBody, Is.SameAs(previous));
        }
        finally { Restore(undo); }
        Assert.That(proof.IsUnchanged(), Is.True);
    }

    [TestCase("method-name")]
    [TestCase("explicit-this")]
    [TestCase("vararg")]
    [TestCase("owner-name")]
    [TestCase("field-name")]
    [TestCase("field-binding")]
    [TestCase("field-type")]
    [TestCase("field-offset")]
    [TestCase("class-layout")]
    [TestCase("owner-module")]
    [TestCase("assembly-name")]
    public void RejectedOutputDeclarationsPreserveThePreviouslyVerifiedBody(string defect)
    {
        var method = _methods[0];
        var proof = method.GetExtraData<X64ScalarStaticConstructorProof.Evidence>(X64ScalarStaticConstructorProof.EvidenceKey)!;
        var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        Assert.That(X64ScalarStaticConstructorRecovery.TryGenerate(method, output), Is.True);
        var previous = output.CilMethodBody;
        var field = proof.StaticField.GetExtraData<FieldDefinition>("AsmResolverField")!;
        var undo = new Stack<Action>();
        try
        {
            switch (defect)
            {
                case "method-name": Change<Utf8String?>(undo, () => output.Name, value => output.Name = value, "ChangedConstructor"); break;
                case "explicit-this": Change(undo, () => output.Signature!.ExplicitThis, value => output.Signature!.ExplicitThis = value, true); break;
                case "vararg": Change(undo, () => output.Signature!.Attributes, value => output.Signature!.Attributes = value, CallingConventionAttributes.VarArg); break;
                case "owner-name": Change<Utf8String?>(undo, () => output.DeclaringType!.Name, value => output.DeclaringType!.Name = value, "ChangedOwner"); break;
                case "field-name": Change<Utf8String?>(undo, () => field.Name, value => field.Name = value, "ChangedField"); break;
                case "field-binding": Change<FieldDefinition?>(undo, () => proof.StaticField.GetExtraData<FieldDefinition>("AsmResolverField"),
                    value => proof.StaticField.PutExtraData("AsmResolverField", value!), null); break;
                case "field-type": Change(undo, () => field.Signature!.FieldType, value => field.Signature!.FieldType = value, _app.SystemTypes.SystemUInt32Type.ToTypeSignature()); break;
                case "field-offset": Change<int?>(undo, () => field.FieldOffset, value => field.FieldOffset = value, 4); break;
                case "class-layout": Change(undo, () => output.DeclaringType!.ClassLayout, value => output.DeclaringType!.ClassLayout = value, new ClassLayout(1, 1)); break;
                case "assembly-name": Change<Utf8String?>(undo, () => output.DeclaringType!.DeclaringModule!.Assembly!.Name,
                    value => output.DeclaringType!.DeclaringModule!.Assembly!.Name = value, "ChangedAssembly"); break;
                case "owner-module":
                    var owner = output.DeclaringType!;
                    Assert.That(owner.DeclaringType, Is.Null, "This control needs a top-level original owner.");
                    var module = owner.DeclaringModule!;
                    var moved = new ModuleDefinition("Changed.dll");
                    undo.Push(() => { moved.TopLevelTypes.Remove(owner); module.TopLevelTypes.Add(owner); });
                    module.TopLevelTypes.Remove(owner); moved.TopLevelTypes.Add(owner); break;
            }
            Assert.That(proof.IsUnchanged(), Is.True);
            Assert.That(X64ScalarStaticConstructorRecovery.TryGenerate(method, output), Is.False);
            Assert.That(output.CilMethodBody, Is.SameAs(previous));
        }
        finally { Restore(undo); }
        Assert.That(X64ScalarStaticConstructorRecovery.TryGenerate(method, output), Is.True);
    }

    [Test]
    public void OriginalSignedInt32WrapperInitializationDoesNotWidenTailCallAdmission()
    {
        if (_synthetic) return;
        var wrappers = _app.Assemblies.Where(assembly => UnityTargetAssemblyScope.Classify(assembly) == UnityTargetAssemblyKind.Application)
            .SelectMany(assembly => assembly.Types).SelectMany(type => type.Methods)
            .Where(method => method.Name == ".cctor")
            .Select(method => (Method: method, Proof: X64ScalarWrapperStaticConstructorProof.Find(method)))
            .Where(item => item.Proof?.ScalarField.FieldType.Type == Il2CppTypeEnum.IL2CPP_TYPE_I4).ToArray();
        Assert.That(wrappers, Is.Not.Empty);
        foreach (var (method, proof) in wrappers)
        {
            Assert.That(X64ScalarWrapperTailCallProof.ScalarField(method.DeclaringType!, allowSignedWord: true), Is.Null);
            var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            Assert.That(X64ScalarWrapperStaticConstructorRecovery.TryGenerate(method, output), Is.True);
            Assert.That(output.CilMethodBody!.Instructions.Single(site => site.OpCode == CilOpCodes.Ldc_I4).Operand,
                Is.EqualTo(unchecked((int)proof!.ValueBits)));
            var previous = output.CilMethodBody;
            var name = output.Name;
            try
            {
                output.Name = "ChangedConstructor";
                Assert.That(X64ScalarWrapperStaticConstructorRecovery.TryGenerate(method, output), Is.False);
                Assert.That(output.CilMethodBody, Is.SameAs(previous));
            }
            finally { output.Name = name; }
            Assert.That(X64ScalarWrapperStaticConstructorRecovery.TryGenerate(method, output), Is.True);
            previous = output.CilMethodBody;
            Assert.That(_app.Binary.TryGetGenericMethodTableRegistration(out var origin), Is.True);
            var registration = _app.Binary.ReadReadableAtVirtualAddress<Il2CppMetadataRegistration>(origin.MetadataRegistrationAddress);
            var offsets = _app.Binary.ReadPointerAtVirtualAddress(registration.fieldOffsetListAddress +
                (ulong)method.DeclaringType!.Definition!.TypeIndex.Value * 8);
            var offsetAddress = offsets + (ulong)proof!.ScalarField.BackingData!.IndexInParent * 4;
            foreach (var rawOffset in new[] { 0, 17 })
            {
                var undo = new Stack<Action>();
                try
                {
                    WriteNative(undo, offsetAddress, BitConverter.GetBytes(rawOffset));
                    Assert.That(X64ScalarWrapperStaticConstructorProof.Find(method), Is.Null,
                        "An offset in the boxed object header or changed scalar offset cannot qualify fresh layout.");
                    Assert.That(X64ScalarWrapperStaticConstructorRecovery.TryGenerate(method, output), Is.False);
                    Assert.That(output.CilMethodBody, Is.SameAs(previous));
                }
                finally { Restore(undo); }
                Assert.That(X64ScalarWrapperStaticConstructorRecovery.TryGenerate(method, output), Is.True);
                previous = output.CilMethodBody;
            }
        }
    }

    private static void Change<T>(Stack<Action> undo, Func<T> read, Action<T> write, T changed)
    {
        var previous = read(); undo.Push(() => write(previous)); write(changed);
    }

    private void MutateNative(Stack<Action> undo, ulong address, byte mask)
    {
        var offset = checked((int)_app.Binary.MapVirtualAddressToRaw(address, false));
        WriteNative(undo, address, [(byte)(_app.Binary.GetRawBinaryContent()[offset] ^ mask)]);
    }

    private void WriteNative(Stack<Action> undo, ulong address, byte[] changed)
    {
        var pe = (PE)_app.Binary;
        Assert.That(pe.BaseStream, Is.InstanceOf<MemoryStream>());
        var offset = checked((int)pe.MapVirtualAddressToRaw(address, false));
        var previous = pe.GetRawBinaryContent().Slice(offset, changed.Length).ToArray();
        void Write(byte[] bytes)
        {
            var position = pe.BaseStream.Position;
            try { pe.BaseStream.Position = offset; pe.BaseStream.Write(bytes); }
            finally { pe.BaseStream.Position = position; }
        }
        undo.Push(() => Write(previous)); Write(changed);
    }

    private static void Restore(Stack<Action> undo)
    {
        var failures = new List<Exception>();
        while (undo.Count > 0)
            try { undo.Pop()(); }
            catch (Exception exception) { failures.Add(exception); }
        if (failures.Count != 0) throw new AggregateException(failures);
    }
}
