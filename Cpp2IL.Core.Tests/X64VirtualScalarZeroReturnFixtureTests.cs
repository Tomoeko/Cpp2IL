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
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils.AsmResolver;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using Utf8String = AsmResolver.Utf8String;

namespace Cpp2IL.Core.Tests;

[TestFixture(false)]
[TestFixture(true)]
[NonParallelizable]
public class X64VirtualScalarZeroReturnFixtureTests(bool synthetic)
{
    private ApplicationAnalysisContext _app = null!;
    private MethodAnalysisContext[] _methods = null!;

    [OneTimeSetUp]
    public void LoadOriginalPlayer()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_VIRTUAL_SCALAR_ZERO_LEAF_FIXTURE_INPUT");
        var binary = synthetic && !string.IsNullOrEmpty(input) ? Path.Combine(input, "GameAssembly.dll") :
            synthetic ? null : Environment.GetEnvironmentVariable("CPP2IL_VIRTUAL_SCALAR_ZERO_BINARY");
        var metadata = synthetic && !string.IsNullOrEmpty(input) ?
            Path.Combine(input, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat") :
            synthetic ? null : Environment.GetEnvironmentVariable("CPP2IL_VIRTUAL_SCALAR_ZERO_METADATA");
        if (string.IsNullOrEmpty(binary) || string.IsNullOrEmpty(metadata))
            Assert.Ignore("Set the virtual scalar-zero original player input paths to execute these controls.");
        Cpp2IlApi.ResetInternalState(); TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(binary!, metadata!, UnityVersion.Parse("2021.3.35f1"));
        _app = Cpp2IlApi.CurrentAppContext!;
        Assert.That(((PE)_app.Binary).BaseStream, Is.InstanceOf<MemoryStream>());
        var assemblies = _app.Assemblies.Where(assembly => !synthetic || assembly.Name == "NativeVirtualScalarZeroLeafFixture").ToArray();
        if (synthetic)
        {
            var types = assemblies.Single().Types.Where(type => type.Name != "<Module>").ToArray();
            Assert.That(types.Length, Is.EqualTo(5));
            Assert.That(types.Sum(type => type.Methods.Count), Is.EqualTo(12));
            Assert.That(types.Sum(type => type.Fields.Count), Is.EqualTo(1));
            Assert.That(types.Sum(type => type.Properties.Count), Is.EqualTo(6));
        }
        _methods = assemblies.SelectMany(assembly => assembly.Types).SelectMany(type => type.Methods)
            .Where(method => method.UnderlyingPointer != 0 && method.IsVirtual && !method.IsAbstract &&
                ReferenceEquals(method.ReturnType, _app.SystemTypes.SystemSingleType))
            .Where(method => X64VirtualScalarZeroReturnProof.Find(method) != null).ToArray();
        Assert.That(_methods, Is.Not.Empty);
        if (synthetic) Assert.That(_methods.Length, Is.EqualTo(5), "Retain both base getters, both overrides and the unused-String implementation.");
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(_app);
        foreach (var method in _methods) method.Analyze();
    }

    [OneTimeTearDown]
    public void ReleasePlayer() => Cpp2IlApi.ResetInternalState();

    private MethodAnalysisContext Method => _methods.FirstOrDefault(method => method.DeclaringType!.DeclaringType == null &&
        method.DeclaringType.InterfaceContexts.Count > 0 && method.DeclaringType.Properties.Any(property => ReferenceEquals(property.Getter, method))) ??
        _methods.First(method => method.DeclaringType!.InterfaceContexts.Count > 0);

    [Test]
    public void CompleteAliasesDispatchAndTypedPositiveZeroSurviveActualAnalysisAndIl()
    {
        foreach (var method in _methods)
        {
            Assert.That(method.AnalysisWarnings, Is.Empty);
            Assert.That(X64VirtualScalarZeroReturnProof.IsValidFor(method), Is.True);
            var proof = method.GetExtraData<X64VirtualScalarZeroReturnProof.Evidence>(X64VirtualScalarZeroReturnProof.EvidenceKey)!;
            Assert.That(proof.MethodImplRowPresenceUnavailable, Is.True,
                "A recovered body does not establish original managed MethodImpl row presence.");
            Assert.That(proof.Aliases.ToArray(), Is.EquivalentTo(_app.MethodsByAddress[method.UnderlyingPointer]));
            var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            Assert.That(X64VirtualScalarZeroReturnProof.MatchesOutput(method, output), Is.True);
            IlGenerator.GenerateIl(method, output);
            Assert.That(output.CilMethodBody!.Instructions.Select(site => site.OpCode),
                Is.EqualTo(new[] { CilOpCodes.Ldc_R4, CilOpCodes.Ret }));
            Assert.That(BitConverter.SingleToInt32Bits((float)output.CilMethodBody.Instructions[0].Operand!), Is.Zero);
            Assert.That(output.CilMethodBody.ExceptionHandlers, Is.Empty);
        }
        if (synthetic)
            Assert.That(_methods.Count(method => method.BaseMethod != null), Is.EqualTo(2),
                "Reuse-slot ordinary overrides remain in the complete fixture denominator.");
    }

    [TestCase("alias-removed")]
    [TestCase("alias-duplicate")]
    [TestCase("generic-pointer")]
    [TestCase("method-slot")]
    [TestCase("virtual-flag")]
    [TestCase("owner-base")]
    [TestCase("cached-interface")]
    [TestCase("cached-override")]
    [TestCase("unused-interface-slot")]
    [TestCase("inherited-interface-cache")]
    [TestCase("raw-interface-offset")]
    [TestCase("raw-vtable")]
    [TestCase("native-body")]
    [TestCase("cached-body")]
    [TestCase("evidence-removed")]
    public void RemovedOrChangedOriginalBindingsRejectFreshAndSavedProof(string defect)
    {
        var method = Method;
        var owner = method.DeclaringType!;
        var undo = new Stack<Action>();
        try
        {
            var aliases = _app.MethodsByAddress[method.UnderlyingPointer];
            switch (defect)
            {
                case "alias-removed":
                    var alias = aliases.FirstOrDefault(candidate => !ReferenceEquals(candidate, method)) ?? method;
                    var ordinal = aliases.IndexOf(alias); undo.Push(() => aliases.Insert(ordinal, alias)); aliases.RemoveAt(ordinal); break;
                case "alias-duplicate": undo.Push(() => aliases.RemoveAt(aliases.Count - 1)); aliases.Add(method); break;
                case "generic-pointer":
                    Assert.That(_app.Binary.TryGetGenericMethodTableRegistration(out var registration), Is.True);
                    Assert.That(registration.MethodPointerCount, Is.GreaterThan(0));
                    WriteNative(undo, registration.MethodPointersAddress, BitConverter.GetBytes(method.UnderlyingPointer)); break;
                case "method-slot": Change(undo, () => method.Definition!.slot, value => method.Definition!.slot = value, ushort.MaxValue); break;
                case "virtual-flag": Change(undo, () => method.Definition!.flags, value => method.Definition!.flags = value,
                    (ushort)(method.Definition!.flags ^ (ushort)MethodAttributes.Virtual)); break;
                case "owner-base": Change(undo, () => owner.OverrideBaseType, value => owner.OverrideBaseType = value, owner); break;
                case "cached-interface":
                    var contract = owner.InterfaceContexts[0]; undo.Push(() => owner.InterfaceContexts.Insert(0, contract)); owner.InterfaceContexts.RemoveAt(0); break;
                case "cached-override":
                    var target = method.Overrides[0]; undo.Push(() => method.Overrides.Insert(0, target)); method.Overrides.RemoveAt(0); break;
                case "unused-interface-slot":
                    var contractMethods = owner.InterfaceContexts[0].Methods;
                    var other = contractMethods.Last(candidate => !method.Overrides.Contains(candidate));
                    Change(undo, () => other.Definition!.slot, value => other.Definition!.slot = value, ushort.MaxValue); break;
                case "inherited-interface-cache":
                    var inherited = method.GetExtraData<X64VirtualScalarZeroReturnProof.Evidence>(X64VirtualScalarZeroReturnProof.EvidenceKey)!
                        .Types.ToArray().FirstOrDefault(type => type.IsInterface && type.InterfaceContexts.Count > 0);
                    if (inherited == null) Assert.Ignore("This optional player has no inherited interface in the selected complete domain.");
                    var parent = inherited!.InterfaceContexts[0];
                    undo.Push(() => inherited.InterfaceContexts.Insert(0, parent)); inherited.InterfaceContexts.RemoveAt(0); break;
                case "raw-interface-offset":
                    var offset = owner.Definition!.InterfaceOffsets.First(row => row.Type.Data.Dummy == (ulong)owner.InterfaceContexts[0].Definition!.TypeIndex.Value);
                    Change(undo, () => offset.offset, value => offset.offset = value, offset.offset + 1); break;
                case "raw-vtable":
                    var index = owner.Definition!.VtableStart + method.Definition!.slot;
                    Change(undo, () => _app.Metadata.VTableMethodIndices[index], value => _app.Metadata.VTableMethodIndices[index] = value, 0U); break;
                case "native-body": WriteNative(undo, method.UnderlyingPointer, [0x90]); break;
                case "cached-body":
                    var bytes = method.RawBytes.AsSpan().ToArray(); bytes[0] ^= 1;
                    Change(undo, () => method.RawBytes, value => method.RawBytes = value, new BinarySlice(bytes)); break;
                case "evidence-removed": Change(undo,
                    () => method.GetExtraData<X64VirtualScalarZeroReturnProof.Evidence>(X64VirtualScalarZeroReturnProof.EvidenceKey),
                    value => method.PutExtraData(X64VirtualScalarZeroReturnProof.EvidenceKey, value!), null); break;
            }
            if (defect != "evidence-removed") Assert.That(X64VirtualScalarZeroReturnProof.Find(method), Is.Null);
            Assert.That(X64VirtualScalarZeroReturnProof.IsValidFor(method), Is.False);
            Reject(method);
        }
        finally { Restore(undo); }
        Assert.That(X64VirtualScalarZeroReturnProof.IsValidFor(method), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CoherentUnusedParameterDescriptorChangeCannotReuseSavedIdentity(bool attributes)
    {
        var method = _methods.SelectMany(candidate => candidate.GetExtraData<X64VirtualScalarZeroReturnProof.Evidence>(X64VirtualScalarZeroReturnProof.EvidenceKey)!.Aliases.ToArray())
            .First(candidate => candidate.Parameters.Count != 0);
        var parameter = method.Parameters[0];
        var raw = parameter.Definition!.RawType!;
        Assert.That(_app.Binary.TryGetTypeVirtualAddress(raw, out var address), Is.True);
        var saved = X64VirtualScalarZeroReturnProof.Find(method)!;
        var undo = new Stack<Action>();
        try
        {
            if (attributes)
            {
                var changed = raw.Bits ^ (uint)ParameterAttributes.Optional;
                Change(undo, () => raw.Bits, value => raw.Bits = value, changed);
                Change(undo, () => raw.Attrs, value => raw.Attrs = value, changed & 0xFFFF);
                WriteNative(undo, address + 8, BitConverter.GetBytes(changed));
            }
            else
            {
                Change(undo, () => raw.Datapoint, value => raw.Datapoint = value, raw.Datapoint + 1);
                Change(undo, () => raw.Data.Dummy, value => raw.Data.Dummy = value, raw.Datapoint);
                WriteNative(undo, address, BitConverter.GetBytes(raw.Datapoint));
            }
            Assert.That(X64VirtualScalarZeroReturnProof.Find(method), Is.Not.Null,
                "The ignored argument still has a fresh original width and ABI; this control isolates saved freshness.");
            Assert.That(saved.IsUnchanged(), Is.False);
        }
        finally { Restore(undo); }
        Assert.That(saved.IsUnchanged(), Is.True);
    }

    [Test]
    public void AnUncalledByrefAncestorSignatureIsBoundWithoutRequiringWrapperObjectIdentity()
    {
        var method = Method;
        var proof = method.GetExtraData<X64VirtualScalarZeroReturnProof.Evidence>(X64VirtualScalarZeroReturnProof.EvidenceKey)!;
        var parameter = proof.Types.ToArray().SelectMany(type => type.Methods).SelectMany(candidate => candidate.Parameters)
            .First(candidate => candidate.ParameterType is ByRefTypeAnalysisContext);
        Assert.That(parameter.ParameterType, Is.Not.SameAs(parameter.DefaultParameterType),
            "The resolver creates byref wrappers on demand; their original element/signature identity remains stable.");
        Assert.That(proof.IsUnchanged(), Is.True);
        var undo = new Stack<Action>();
        try
        {
            Change(undo, () => parameter.OverrideParameterType, value => parameter.OverrideParameterType = value,
                _app.SystemTypes.SystemObjectType);
            Assert.That(X64VirtualScalarZeroReturnProof.Find(method), Is.Null);
            Reject(method);
        }
        finally { Restore(undo); }
        Assert.That(proof.IsUnchanged(), Is.True);
    }

    [TestCase("negative-zero")]
    [TestCase("wrong-width")]
    [TestCase("return-site")]
    [TestCase("integer-width")]
    public void TypedReturnCannotLoseItsNativeAddressWidthOrPositiveSign(string defect)
    {
        var method = Method;
        var returned = method.ControlFlowGraph!.Instructions.Single();
        var undo = new Stack<Action>();
        try
        {
            switch (defect)
            {
                case "negative-zero": Change<IOperand>(undo, () => returned.Operands[0], value => returned.SetOperand(0, value),
                    new FloatLiteral(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)))); break;
                case "wrong-width": Change<IOperand>(undo, () => returned.Operands[0], value => returned.SetOperand(0, value), new DoubleLiteral(0)); break;
                case "return-site": Change(undo, () => returned.NativeAddress, value => returned.NativeAddress = value, returned.NativeAddress + 1); break;
                case "integer-width": Change(undo, () => returned.IntegerBitWidth, value => returned.IntegerBitWidth = value, 32); break;
            }
            Assert.That(X64VirtualScalarZeroReturnProof.IsValidFor(method), Is.False);
            Reject(method);
        }
        finally { Restore(undo); }
        Assert.That(X64VirtualScalarZeroReturnProof.IsValidFor(method), Is.True);
    }

    [TestCase("method-name")]
    [TestCase("vararg")]
    [TestCase("base-type")]
    [TestCase("interface-target")]
    [TestCase("property-getter")]
    [TestCase("explicit-row")]
    [TestCase("assembly-name")]
    [TestCase("owner-module")]
    [TestCase("extra-property")]
    [TestCase("transport-getter-target")]
    public void ChangedOutputDispatchOrAccessorPreservesThePreviouslyPublishedBody(string defect)
    {
        var method = Method;
        var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        IlGenerator.GenerateIl(method, output);
        var previous = output.CilMethodBody;
        var owner = output.DeclaringType!;
        var undo = new Stack<Action>();
        try
        {
            switch (defect)
            {
                case "method-name": Change<Utf8String?>(undo, () => output.Name, value => output.Name = value, "ChangedMethod"); break;
                case "vararg": Change(undo, () => output.Signature!.Attributes, value => output.Signature!.Attributes = value,
                    CallingConventionAttributes.VarArg | CallingConventionAttributes.HasThis); break;
                case "base-type": Change(undo, () => owner.BaseType, value => owner.BaseType = value, null); break;
                case "interface-target": Change(undo, () => owner.Interfaces[0].Interface, value => owner.Interfaces[0].Interface = value,
                    _app.SystemTypes.SystemObjectType.ToTypeSignature().ToTypeDefOrRef()); break;
                case "property-getter":
                    var property = method.DeclaringType!.Properties.FirstOrDefault(candidate => ReferenceEquals(candidate.Getter, method));
                    if (property == null) Assert.Ignore("This optional alias is not a property getter.");
                    var written = property!.GetExtraData<PropertyDefinition>("AsmResolverProperty")!;
                    var semantics = written.Semantics.ToArray();
                    undo.Push(() => { written.Semantics.Clear(); foreach (var item in semantics) written.Semantics.Add(item); });
                    written.Semantics.Clear(); break;
                case "explicit-row":
                    var row = new MethodImplementation((IMethodDefOrRef)method.Overrides[0].ToMethodDescriptor(), output);
                    undo.Push(() => owner.MethodImplementations.Remove(row)); owner.MethodImplementations.Add(row); break;
                case "assembly-name":
                    var assembly = owner.DeclaringModule!.Assembly!;
                    Change<Utf8String?>(undo, () => assembly.Name, value => assembly.Name = value, "ChangedAssembly"); break;
                case "owner-module":
                    var module = owner.DeclaringModule!; var position = module.TopLevelTypes.IndexOf(owner);
                    if (position < 0) Assert.Ignore("Use an ordinary top-level owner for this mutation.");
                    var replacement = new ModuleDefinition("Other.dll");
                    undo.Push(() => { replacement.TopLevelTypes.Remove(owner); module.TopLevelTypes.Insert(position, owner); });
                    module.TopLevelTypes.RemoveAt(position); replacement.TopLevelTypes.Add(owner); break;
                case "extra-property":
                    var extra = new PropertyDefinition("Extra", AsmResolver.PE.DotNet.Metadata.Tables.PropertyAttributes.None,
                        PropertySignature.CreateInstance(_app.SystemTypes.SystemSingleType.ToTypeSignature()));
                    undo.Push(() => owner.Properties.Remove(extra)); owner.Properties.Add(extra); break;
                case "transport-getter-target":
                    var captured = method.GetExtraData<X64VirtualScalarZeroReturnProof.Evidence>(X64VirtualScalarZeroReturnProof.EvidenceKey)!
                        .Types.ToArray().FirstOrDefault(type => type.GetExtraData<TypeDefinition>("AsmResolverType")!.Properties.Count > type.Properties.Count);
                    if (captured == null) Assert.Ignore("This optional player closure has no generated explicit getter property.");
                    var transported = captured!.GetExtraData<TypeDefinition>("AsmResolverType")!.Properties[captured.Properties.Count];
                    var methods = transported.Semantics.ToArray();
                    undo.Push(() => { transported.Semantics.Clear(); foreach (var item in methods) transported.Semantics.Add(item); });
                    transported.Semantics.Clear(); break;
            }
            Assert.That(X64VirtualScalarZeroReturnProof.IsValidFor(method), Is.True);
            Assert.That(X64VirtualScalarZeroReturnProof.MatchesOutput(method, output), Is.False);
            Reject(method);
            Assert.That(output.CilMethodBody, Is.SameAs(previous));
        }
        finally { Restore(undo); }
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, output));
    }

    private static void Change<T>(Stack<Action> undo, Func<T> read, Action<T> write, T value)
    {
        var previous = read(); undo.Push(() => write(previous)); write(value);
    }

    private void WriteNative(Stack<Action> undo, ulong address, byte[] bytes)
    {
        var pe = (PE)_app.Binary;
        Assert.That(pe.BaseStream, Is.InstanceOf<MemoryStream>(), "Never alter an original player file on disk.");
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

    private static void Reject(MethodAnalysisContext method)
    {
        Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, method.GetExtraData<MethodDefinition>("AsmResolverMethod")!));
    }

    private static void Restore(Stack<Action> undo)
    {
        Exception? failure = null;
        while (undo.Count != 0)
            try { undo.Pop()(); } catch (Exception exception) { failure ??= exception; }
        if (failure != null) throw new InvalidOperationException("A mutation restoration failed.", failure);
    }
}
