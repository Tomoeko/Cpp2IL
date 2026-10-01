using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.SourceEmission;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;
using ManagedTypeAttributes = AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes;
using ManagedFieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;

namespace Cpp2IL.Core.Tests;

[TestFixture(true)]
[TestFixture(false)]
[NonParallelizable]
public class X64BeforeFieldInitBaseConstructorFixtureTests(bool synthetic)
{
    private ApplicationAnalysisContext _app = null!;
    private MethodAnalysisContext[] _methods = null!;

    [OneTimeSetUp]
    public void LoadOriginalPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_VIRTUAL_SCALAR_ZERO_LEAF_FIXTURE_INPUT");
        var binary = synthetic && !string.IsNullOrEmpty(directory) ? Path.Combine(directory, "GameAssembly.dll") :
            synthetic ? null : Environment.GetEnvironmentVariable("CPP2IL_VIRTUAL_SCALAR_ZERO_BINARY");
        var metadata = synthetic && !string.IsNullOrEmpty(directory) ? Path.Combine(directory,
            "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat") :
            synthetic ? null : Environment.GetEnvironmentVariable("CPP2IL_VIRTUAL_SCALAR_ZERO_METADATA");
        if (string.IsNullOrEmpty(binary) || string.IsNullOrEmpty(metadata))
            Assert.Ignore("Configure an original exact-target player for the guarded base-constructor controls.");
        Cpp2IlApi.ResetInternalState(); TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(binary!, metadata!, UnityVersion.Parse("2021.3.35f1"));
        _app = Cpp2IlApi.CurrentAppContext!;
        Assert.That(((PE)_app.Binary).BaseStream, Is.InstanceOf<MemoryStream>(), "Native mutations must remain in memory.");
        var assemblies = _app.Assemblies.Where(assembly => synthetic ? assembly.Name == "NativeVirtualScalarZeroLeafFixture" :
            UnityTargetAssemblyScope.Classify(assembly) == UnityTargetAssemblyKind.Application).ToArray();
        _methods = assemblies.SelectMany(assembly => assembly.Types).SelectMany(type => type.Methods)
            .Where(method => method.Name == ".ctor" && X64GuardedBaseConstructorProof.FindBeforeFieldInit(method) != null).ToArray();
        if (!synthetic && _methods.Length == 0)
            Assert.Ignore("This input has no qualified complete BeforeFieldInit base-constructor route.");
        Assert.That(_methods, Is.Not.Empty);
        if (synthetic)
        {
            var types = assemblies.Single().Types.Where(type => type.Name != "<Module>").ToArray();
            Assert.That(types.Length, Is.EqualTo(5));
            Assert.That(types.Sum(type => type.Methods.Count), Is.EqualTo(12));
            Assert.That(types.Sum(type => type.Fields.Count), Is.EqualTo(1));
            Assert.That(types.Sum(type => type.Properties.Count), Is.EqualTo(6));
            Assert.That(_methods.Length, Is.EqualTo(1));
        }
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(_app);
        foreach (var method in _methods)
        {
            var proof = X64GuardedBaseConstructorProof.FindBeforeFieldInit(method)!;
            // Exercise the real initializer producer separately from the caller.
            Assert.That(X64ScalarStaticConstructorRecovery.TryGenerate(proof.ClassConstructor,
                proof.ClassConstructor.GetExtraData<MethodDefinition>("AsmResolverMethod")!), Is.True);
            proof.BaseConstructor.Analyze();
            IlGenerator.GenerateIl(proof.BaseConstructor, proof.BaseConstructor.GetExtraData<MethodDefinition>("AsmResolverMethod")!);
            method.Analyze();
        }
    }

    [OneTimeTearDown]
    public void ReleaseOriginalPlayer() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void ActualAnalysisAndIlRetainOnlyTheOriginalImmediateBaseCall()
    {
        foreach (var method in _methods)
        {
            var proof = Proof(method);
            Assert.That(proof.IsUnchanged(), Is.True);
            Assert.That(method.ControlFlowGraph!.Instructions.Any(site => site.OpCode is
                ISIL.OpCode.Invalid or ISIL.OpCode.NotImplemented or ISIL.OpCode.UnresolvedValue or
                ISIL.OpCode.Phi or ISIL.OpCode.Interrupt), Is.False);
            Assert.That(method.AnalysisWarnings, Is.Empty);
            Assert.That(X64BeforeFieldInitBaseConstructorRecovery.IsValidFor(method), Is.True);
            Assert.That(proof.BaseConstructor.DeclaringType, Is.SameAs(method.DeclaringType!.BaseType));
            Assert.That(proof.BaseConstructor.UnderlyingPointer, Is.Not.EqualTo(proof.Native.Tail),
                "The complete inert base thunk is inlined into this native tail, not omitted from managed construction.");
            var output = Output(method);
            IlGenerator.GenerateIl(method, output);
            Assert.That(output.CilMethodBody!.Instructions.Select(site => site.OpCode),
                Is.EqualTo(new[] { CilOpCodes.Ldarg, CilOpCodes.Call, CilOpCodes.Ret }));
            Assert.That(output.CilMethodBody.Instructions[0].Operand, Is.SameAs(output.Parameters.ThisParameter));
            Assert.That(output.CilMethodBody.Instructions[1].Operand,
                Is.SameAs(proof.BaseConstructor.GetExtraData<MethodDefinition>("AsmResolverMethod")));
            Assert.That(output.CilMethodBody.ExceptionHandlers, Is.Empty);
            Assert.That(proof.BaseConstructor.DeclaringType!.Attributes & TypeAttributes.BeforeFieldInit, Is.EqualTo(TypeAttributes.BeforeFieldInit));
        }
    }

    [TestCase("caller-native")]
    [TestCase("caller-cache")]
    [TestCase("base-native")]
    [TestCase("helper")]
    [TestCase("class-export")]
    [TestCase("type-slot")]
    [TestCase("base-flag")]
    [TestCase("owner-flag")]
    [TestCase("owner-base")]
    [TestCase("base-alias-missing")]
    [TestCase("base-alias-duplicate")]
    [TestCase("coherent-generic-alias-missing")]
    [TestCase("cctor-cache")]
    [TestCase("cctor-value-coherent")]
    [TestCase("evidence-missing")]
    public void LostOrChangedOriginalEvidenceCannotPublishAReplacementBody(string defect)
    {
        var method = _methods[0]; var proof = Proof(method); var output = Output(method);
        IlGenerator.GenerateIl(method, output); var previous = output.CilMethodBody;
        var undo = new Stack<Action>();
        try
        {
            switch (defect)
            {
                case "caller-native": MutateNative(undo, method.UnderlyingPointer); break;
                case "caller-cache": ChangeCachedByte(undo, method, 0, (byte)(method.RawBytes.AsSpan()[0] ^ 1)); break;
                case "base-native": MutateNative(undo, proof.BaseConstructor.UnderlyingPointer); break;
                case "helper": MutateNative(undo, proof.Native.MetadataInitializer); break;
                case "class-export": MutateNative(undo, proof.Native.ClassInitializer); break;
                case "type-slot": MutateNative(undo, proof.Native.TypeInfoSlot); break;
                case "base-flag": Change(undo, () => proof.BaseConstructor.DeclaringType!.OverrideAttributes,
                    value => proof.BaseConstructor.DeclaringType!.OverrideAttributes = value,
                    proof.BaseConstructor.DeclaringType!.Attributes & ~TypeAttributes.BeforeFieldInit); break;
                case "owner-flag": Change(undo, () => method.DeclaringType!.OverrideAttributes,
                    value => method.DeclaringType!.OverrideAttributes = value, method.DeclaringType!.Attributes & ~TypeAttributes.BeforeFieldInit); break;
                case "owner-base": Change(undo, () => method.DeclaringType!.OverrideBaseType,
                    value => method.DeclaringType!.OverrideBaseType = value, _app.SystemTypes.SystemObjectType); break;
                case "base-alias-missing":
                    var aliases = _app.MethodsByAddress[proof.BaseConstructor.UnderlyingPointer];
                    var alias = aliases.Last(); var index = aliases.Count - 1;
                    undo.Push(() => aliases.Insert(index, alias)); aliases.RemoveAt(index); break;
                case "base-alias-duplicate":
                    var duplicate = _app.MethodsByAddress[proof.BaseConstructor.UnderlyingPointer];
                    undo.Push(() => duplicate.RemoveAt(duplicate.Count - 1)); duplicate.Add(proof.BaseConstructor); break;
                case "coherent-generic-alias-missing":
                    var concrete = _app.MethodsByAddress[proof.BaseConstructor.UnderlyingPointer]
                        .OfType<ConcreteGenericMethodAnalysisContext>().FirstOrDefault();
                    if (concrete == null) Assert.Ignore("This original base thunk has no generic registration alias to mutate.");
                    var reference = concrete!.MethodRef!;
                    var group = _app.Binary.ConcreteGenericMethods[reference.BaseMethod]; var position = group.IndexOf(reference);
                    var cached = _app.MethodsByAddress[concrete.UnderlyingPointer]; var cachedIndex = cached.IndexOf(concrete);
                    undo.Push(() => group.Insert(position, reference)); group.RemoveAt(position);
                    undo.Push(() => _app.ConcreteGenericMethodsByRef.Add(reference, concrete)); _app.ConcreteGenericMethodsByRef.Remove(reference);
                    undo.Push(() => cached.Insert(cachedIndex, concrete)); cached.RemoveAt(cachedIndex); break;
                case "cctor-cache":
                    ChangeCachedByte(undo, proof.ClassConstructor, 0, (byte)(proof.ClassConstructor.RawBytes.AsSpan()[0] ^ 1)); break;
                case "cctor-value-coherent":
                    var store = X86Utils.Iterate(proof.ClassConstructor).Single(site => site.Code == Iced.Intel.Code.Mov_rm32_imm32);
                    var address = store.NextIP - 4; var byteIndex = checked((int)(address - proof.ClassConstructor.UnderlyingPointer));
                    var changed = (byte)(proof.ClassConstructor.RawBytes.AsSpan()[byteIndex] ^ 1);
                    WriteNative(undo, address, [changed]);
                    ChangeCachedByte(undo, proof.ClassConstructor, byteIndex, changed);
                    Assert.That(X64GuardedBaseConstructorProof.FindBeforeFieldInit(method), Is.Not.Null,
                        "A coherent new literal is eligible for a fresh proof but must invalidate the saved initializer effects."); break;
                case "evidence-missing":
                    undo.Push(() => method.PutExtraData(X64GuardedBaseConstructorProof.BeforeFieldInitEvidenceKey, proof));
                    method.PutExtraData<X64GuardedBaseConstructorProof.BeforeFieldInitEvidence>(
                        X64GuardedBaseConstructorProof.BeforeFieldInitEvidenceKey, null!); break;
            }
            if (defect is not ("evidence-missing" or "cctor-value-coherent"))
                Assert.That(X64GuardedBaseConstructorProof.FindBeforeFieldInit(method), Is.Null);
            if (defect != "evidence-missing") Assert.That(proof.IsUnchanged(), Is.False);
            Assert.That(X64BeforeFieldInitBaseConstructorRecovery.TryGenerate(method, output), Is.False);
            Assert.That(output.CilMethodBody, Is.SameAs(previous));
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, output));
            Assert.That(output.CilMethodBody, Is.SameAs(previous));
        }
        finally { Restore(undo); }
        Assert.That(proof.IsUnchanged(), Is.True);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, output));
    }

    [TestCase("assembly-name")]
    [TestCase("module-name")]
    [TestCase("owner-flag")]
    [TestCase("base-flag")]
    [TestCase("base-signature")]
    [TestCase("cctor-name")]
    [TestCase("cctor-value")]
    [TestCase("readonly-field")]
    [TestCase("base-body")]
    [TestCase("field-constant")]
    [TestCase("field-rva")]
    [TestCase("field-marshal")]
    [TestCase("owner-base")]
    public void ChangedOutputInitializationContractCannotReplaceTheBody(string defect)
    {
        var method = _methods[0]; var proof = Proof(method); var output = Output(method);
        IlGenerator.GenerateIl(method, output); var previous = output.CilMethodBody;
        var owner = output.DeclaringType!;
        var baseOutput = proof.BaseConstructor.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        var cctor = proof.ClassConstructor.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        var field = proof.StaticField.GetExtraData<FieldDefinition>("AsmResolverField")!;
        var undo = new Stack<Action>();
        try
        {
            switch (defect)
            {
                case "assembly-name": Change(undo, () => owner.DeclaringModule!.Assembly!.Name, value => owner.DeclaringModule!.Assembly!.Name = value,
                    new AsmResolver.Utf8String("ChangedAssembly")); break;
                case "module-name": Change(undo, () => owner.DeclaringModule!.Name, value => owner.DeclaringModule!.Name = value,
                    new AsmResolver.Utf8String("ChangedModule.dll")); break;
                case "owner-flag": Change(undo, () => owner.Attributes, value => owner.Attributes = value, owner.Attributes & ~ManagedTypeAttributes.BeforeFieldInit); break;
                case "base-flag": Change(undo, () => baseOutput.DeclaringType!.Attributes,
                    value => baseOutput.DeclaringType!.Attributes = value, baseOutput.DeclaringType!.Attributes & ~ManagedTypeAttributes.BeforeFieldInit); break;
                case "base-signature": Change(undo, () => baseOutput.Signature!.Attributes, value => baseOutput.Signature!.Attributes = value,
                    CallingConventionAttributes.HasThis | CallingConventionAttributes.ExplicitThis); break;
                case "cctor-name": Change(undo, () => cctor.Name, value => cctor.Name = value, new AsmResolver.Utf8String("ChangedInitializer")); break;
                case "cctor-value":
                    var instruction = cctor.CilMethodBody!.Instructions[0];
                    Change(undo, () => instruction.Operand, value => instruction.Operand = value, unchecked((int)proof.InitializerValueBits) ^ 1); break;
                case "base-body":
                    var baseCall = baseOutput.CilMethodBody!.Instructions[1];
                    Change(undo, () => baseCall.OpCode, value => baseCall.OpCode = value, CilOpCodes.Throw); break;
                case "field-constant":
                    Change<Constant?>(undo, () => field.Constant, value => field.Constant = value, Constant.FromValue(17));
                    Change(undo, () => field.Attributes, value => field.Attributes = value, ManagedFieldAttributes.Public | ManagedFieldAttributes.Static | ManagedFieldAttributes.InitOnly); break;
                case "field-rva":
                    Change(undo, () => field.FieldRva, value => field.FieldRva = value, new AsmResolver.DataSegment(new byte[4]));
                    Change(undo, () => field.Attributes, value => field.Attributes = value, ManagedFieldAttributes.Public | ManagedFieldAttributes.Static | ManagedFieldAttributes.InitOnly); break;
                case "field-marshal":
                    Change<MarshalDescriptor?>(undo, () => field.MarshalDescriptor, value => field.MarshalDescriptor = value, new SimpleMarshalDescriptor(NativeType.I4));
                    Change(undo, () => field.Attributes, value => field.Attributes = value, ManagedFieldAttributes.Public | ManagedFieldAttributes.Static | ManagedFieldAttributes.InitOnly); break;
                case "readonly-field": Change(undo, () => field.Attributes, value => field.Attributes = value, field.Attributes & ~ManagedFieldAttributes.InitOnly); break;
                case "owner-base": Change(undo, () => owner.BaseType, value => owner.BaseType = value, owner); break;
            }
            Assert.That(proof.IsUnchanged(), Is.True, "Output corruption is distinct from original input freshness.");
            Assert.That(X64BeforeFieldInitBaseConstructorRecovery.MatchesOutput(method, output), Is.False);
            Assert.That(X64BeforeFieldInitBaseConstructorRecovery.TryGenerate(method, output), Is.False);
            Assert.That(output.CilMethodBody, Is.SameAs(previous));
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, output));
            Assert.That(output.CilMethodBody, Is.SameAs(previous));
        }
        finally { Restore(undo); }
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, output));
    }

    private static MethodDefinition Output(MethodAnalysisContext method) => method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
    private static X64GuardedBaseConstructorProof.BeforeFieldInitEvidence Proof(MethodAnalysisContext method) =>
        method.GetExtraData<X64GuardedBaseConstructorProof.BeforeFieldInitEvidence>(X64GuardedBaseConstructorProof.BeforeFieldInitEvidenceKey)!;

    private static void Change<T>(Stack<Action> undo, Func<T> read, Action<T> write, T changed)
    {
        var previous = read(); undo.Push(() => write(previous)); write(changed);
    }

    private static void ChangeCachedByte(Stack<Action> undo, MethodAnalysisContext method, int index, byte value)
    {
        var bytes = method.RawBytes.AsSpan().ToArray(); bytes[index] = value;
        Change(undo, () => method.RawBytes, changed => method.RawBytes = changed, new BinarySlice(bytes));
    }

    private void MutateNative(Stack<Action> undo, ulong address)
    {
        var offset = checked((int)_app.Binary.MapVirtualAddressToRaw(address, false));
        WriteNative(undo, address, [(byte)(_app.Binary.GetRawBinaryContent()[offset] ^ 1)]);
    }

    private void WriteNative(Stack<Action> undo, ulong address, byte[] bytes)
    {
        var pe = (PE)_app.Binary;
        Assert.That(pe.BaseStream, Is.InstanceOf<MemoryStream>());
        var offset = checked((int)pe.MapVirtualAddressToRaw(address, false));
        var previous = pe.GetRawBinaryContent().Slice(offset, bytes.Length).ToArray();
        void Write(byte[] value)
        {
            var position = pe.BaseStream.Position;
            try { pe.BaseStream.Position = offset; pe.BaseStream.Write(value); }
            finally { pe.BaseStream.Position = position; }
        }
        undo.Push(() => Write(previous)); Write(bytes);
    }

    private static void Restore(Stack<Action> undo)
    {
        var failures = new List<Exception>();
        while (undo.Count != 0) try { undo.Pop()(); } catch (Exception failure) { failures.Add(failure); }
        if (failures.Count != 0) throw new AggregateException(failures);
    }
}
