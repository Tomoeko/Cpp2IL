using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils.AsmResolver;
using LibCpp2IL;
using LibCpp2IL.PE;
using Utf8String = AsmResolver.Utf8String;
using ManagedInstruction = Cpp2IL.Core.ISIL.Instruction;
using ManagedRegister = Cpp2IL.Core.ISIL.Register;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64StackAggregateCallFixtureTests
{
    private ApplicationAnalysisContext _app = null!;
    private MethodAnalysisContext _method = null!;
    private X64StackAggregateCallProof.Evidence _proof = null!;

    [OneTimeSetUp]
    public void LoadWholeOriginalFixture()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_STACK_AGGREGATE_ARGUMENT_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input)) Assert.Ignore("Set the stack aggregate argument original player input path to run these controls.");
        Cpp2IlApi.ResetInternalState(); TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
            Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        _app = Cpp2IlApi.CurrentAppContext!;
        Assert.That(((PE)_app.Binary).BaseStream, Is.InstanceOf<MemoryStream>());
        var types = _app.Assemblies.Single(assembly => assembly.Name == "NativeStackAggregateArgumentFixture")
            .Types.Where(type => type.Name != "<Module>").ToArray();
        Assert.That(types.Length, Is.EqualTo(2));
        Assert.That(types.Sum(type => type.Methods.Count), Is.EqualTo(2));
        Assert.That(types.Sum(type => type.Fields.Count), Is.EqualTo(3));
        _method = types.SelectMany(type => type.Methods).Single(method => method.Name == "Forward");
        _proof = X64StackAggregateCallProof.Find(_method)!;
        Assert.That(_proof, Is.Not.Null, "The original native fixture must establish the byte-copy recipe independently.");
        Assert.That(_proof.Sites.Length, Is.EqualTo(1));
        Assert.That(_proof.Sites[0].Target.Name, Is.EqualTo("Sum"));
        Assert.That(_proof.Sites[0].TargetHasHiddenResult, Is.False);
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(_app);
        _method.Analyze();
        Assert.That(X64StackAggregateCallProof.HasEvidence(_method), Is.True);
    }

    [OneTimeTearDown]
    public void ReleaseOriginalPlayer() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void NativeBytesBecomeOneTypedByValueArgumentThroughRealAnalysisAndIl()
    {
        Assert.That(_method.AnalysisWarnings, Is.Empty);
        var output = _method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        Assert.That(X64StackAggregateCallRecovery.MatchesOutput(_method, output), Is.True);
        IlGenerator.GenerateIl(_method, output);
        var calls = output.CilMethodBody!.Instructions.Where(instruction => instruction.OpCode == CilOpCodes.Call).ToArray();
        Assert.That(calls.Length, Is.EqualTo(1));
        Assert.That(calls[0].Operand, Is.SameAs(_proof.Sites[0].Target.GetExtraData<MethodDefinition>("AsmResolverMethod")));
        Assert.That(output.CilMethodBody.Instructions.Any(instruction => instruction.OpCode is var code &&
            (code == CilOpCodes.Ldloca || code == CilOpCodes.Ldloca_S || code == CilOpCodes.Ldarga || code == CilOpCodes.Ldarga_S)), Is.False,
            "A proved native private argument copy must remain a managed by-value argument.");
        Assert.That(output.CilMethodBody.Instructions.Any(instruction => instruction.OpCode == CilOpCodes.Conv_R8), Is.False);
    }

    [TestCase("caller-alias")]
    [TestCase("callee-alias")]
    [TestCase("caller-pointer")]
    [TestCase("callee-pointer")]
    [TestCase("unlisted-native-alias")]
    [TestCase("unlisted-interior-entry")]
    [TestCase("unlisted-generic-alias")]
    [TestCase("native-module-table")]
    [TestCase("cached-module-table")]
    [TestCase("parameter-type")]
    [TestCase("field-offset")]
    [TestCase("field-type")]
    [TestCase("native-body")]
    [TestCase("cached-body")]
    public void ChangedOriginalByteLayoutParameterOrBindingRejectsFreshAndSavedEvidence(string defect)
    {
        var undo = new Stack<Action>();
        var site = _proof.Sites[0];
        try
        {
            switch (defect)
            {
                case "caller-alias": AddAlias(undo, _method); break;
                case "callee-alias": AddAlias(undo, site.Target); break;
                case "caller-pointer": ChangePointer(undo, _method); break;
                case "callee-pointer": ChangePointer(undo, site.Target); break;
                case "unlisted-native-alias": AddUnlistedNativeBinding(undo, _method.UnderlyingPointer); break;
                case "unlisted-interior-entry": AddUnlistedNativeBinding(undo, site.Copy.FirstRead); break;
                case "unlisted-generic-alias":
                    Assert.That(_app.Binary.TryGetGenericMethodTableRegistration(out var registration), Is.True);
                    Assert.That(registration.MethodPointerCount, Is.GreaterThan(0));
                    WriteNative(undo, registration.MethodPointersAddress, BitConverter.GetBytes(_method.UnderlyingPointer)); break;
                case "native-module-table": ChangeUnrelatedModuleSlot(undo, true); break;
                case "cached-module-table": ChangeUnrelatedModuleSlot(undo, false); break;
                case "parameter-type": Change(undo, () => _method.Parameters[0].OverrideParameterType,
                    value => _method.Parameters[0].OverrideParameterType = value, _app.SystemTypes.SystemSingleType); break;
                case "field-offset": Change(undo, () => site.Type.Fields[0].OverrideOffset,
                    value => site.Type.Fields[0].OverrideOffset = value, 4); break;
                case "field-type": Change(undo, () => site.Type.Fields[0].OverrideFieldType,
                    value => site.Type.Fields[0].OverrideFieldType = value, _app.SystemTypes.SystemInt32Type); break;
                case "native-body": WriteNative(undo, site.Copy.LowRead, [0x90]); break;
                case "cached-body":
                    var bytes = _method.RawBytes.AsSpan().ToArray(); bytes[0] ^= 1;
                    Change(undo, () => _method.RawBytes, value => _method.RawBytes = value, new BinarySlice(bytes)); break;
            }
            Assert.That(X64StackAggregateCallProof.Find(_method), Is.Null);
            Assert.That(_proof.IsUnchanged(), Is.False);
            RejectPreservingBody();
        }
        finally { Restore(undo); }
        Assert.That(_proof.IsUnchanged(), Is.True);
    }

    [TestCase("method-name")]
    [TestCase("method-impl")]
    [TestCase("vararg")]
    [TestCase("parameter-name")]
    [TestCase("target-signature")]
    [TestCase("assembly-name")]
    [TestCase("aggregate-base")]
    [TestCase("aggregate-layout")]
    [TestCase("field-offset")]
    [TestCase("field-mapping")]
    [TestCase("field-constant")]
    [TestCase("field-rva")]
    [TestCase("field-marshal")]
    public void OutputDeclarationMutationsCannotRedirectTheTypedSnapshot(string defect)
    {
        var output = _method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        var site = _proof.Sites[0];
        var aggregate = site.Type.GetExtraData<TypeDefinition>("AsmResolverType")!;
        var field = aggregate.Fields[0];
        var attributes = field.Attributes;
        Assert.That(field.IsStatic, Is.False);
        var undo = new Stack<Action>();
        try
        {
            switch (defect)
            {
                case "method-name": Change<Utf8String?>(undo, () => output.Name, value => output.Name = value, "Changed"); break;
                case "method-impl": Change(undo, () => output.ImplAttributes, value => output.ImplAttributes = value,
                    output.ImplAttributes ^ AsmResolver.PE.DotNet.Metadata.Tables.MethodImplAttributes.Synchronized); break;
                case "vararg": Change(undo, () => output.Signature!.Attributes, value => output.Signature!.Attributes = value,
                    CallingConventionAttributes.VarArg); break;
                case "parameter-name": Change<Utf8String?>(undo, () => output.ParameterDefinitions[0].Name,
                    value => output.ParameterDefinitions[0].Name = value, "Changed"); break;
                case "target-signature":
                    var target = site.Target.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                    Change(undo, () => target.Signature!.ParameterTypes[0], value => target.Signature!.ParameterTypes[0] = value,
                        _app.SystemTypes.SystemInt32Type.ToTypeSignature()); break;
                case "assembly-name":
                    var assembly = aggregate.DeclaringModule!.Assembly!;
                    Change<Utf8String?>(undo, () => assembly.Name, value => assembly.Name = value, "Changed"); break;
                case "aggregate-base": Change(undo, () => aggregate.BaseType, value => aggregate.BaseType = value, null); break;
                case "aggregate-layout": Change(undo, () => aggregate.ClassLayout, value => aggregate.ClassLayout = value,
                    new ClassLayout(1, 16)); break;
                case "field-offset": Change(undo, () => aggregate.Fields[0].FieldOffset,
                    value => aggregate.Fields[0].FieldOffset = value, 8); break;
                case "field-mapping": Change(undo, () => site.Type.Fields[0].GetExtraData<FieldDefinition>("AsmResolverField"),
                    value => site.Type.Fields[0].PutExtraData("AsmResolverField", value!), aggregate.Fields[1]); break;
                case "field-constant":
                    Change<Constant?>(undo, () => field.Constant, value => field.Constant = value, Constant.FromValue(17f));
                    Change(undo, () => field.Attributes, value => field.Attributes = value, attributes); break;
                case "field-rva":
                    Change(undo, () => field.FieldRva, value => field.FieldRva = value, new AsmResolver.DataSegment(new byte[4]));
                    Change(undo, () => field.Attributes, value => field.Attributes = value, attributes); break;
                case "field-marshal":
                    Change<MarshalDescriptor?>(undo, () => field.MarshalDescriptor, value => field.MarshalDescriptor = value,
                        new SimpleMarshalDescriptor(NativeType.R4));
                    Change(undo, () => field.Attributes, value => field.Attributes = value, attributes); break;
            }
            Assert.That(field.Attributes, Is.EqualTo(attributes), "Attached records must be checked independently of field flags.");
            Assert.That(_proof.IsUnchanged(), Is.True);
            Assert.That(X64StackAggregateCallRecovery.MatchesOutput(_method, output), Is.False);
            RejectPreservingBody();
        }
        finally { Restore(undo); }
        Assert.That(X64StackAggregateCallRecovery.MatchesOutput(_method, output), Is.True);
    }

    [Test]
    public void AnIncompleteLiftedNativePrefixCannotReuseACompleteRootProof()
    {
        var native = _proof.Body[..^2].ToArray();
        var original = new ManagedRegister(null, "original");
        var instructions = _proof.Sites.ToArray().SelectMany(site => site.Copy.ReplacedAddresses.Append(site.Copy.Address))
            .Select((address, index) => new ManagedInstruction(index, ISIL.OpCode.Move, original, original) { NativeAddress = address }).ToList();
        Assert.That(X64StackAggregateCallRecovery.TryRewriteArguments(_method, native, instructions), Is.False);
        Assert.That(instructions.All(instruction => instruction.OpCode == ISIL.OpCode.Move &&
            instruction.Operands.All(operand => operand.Equals(original))), Is.True);
    }

    [TestCase("removed-evidence")]
    [TestCase("changed-target")]
    [TestCase("changed-argument")]
    [TestCase("suppressed-effect")]
    public void ChangedRetainedGraphCannotReuseTheOriginalCopyProof(string defect)
    {
        var site = _proof.Sites[0];
        var undo = new Stack<Action>();
        try
        {
            var call = _method.ControlFlowGraph!.Instructions.Single(instruction => instruction.NativeAddress == site.Copy.Call && instruction.IsCall);
            switch (defect)
            {
                case "removed-evidence": Change(undo, () => _method.GetExtraData<X64StackAggregateCallProof.Evidence>(X64StackAggregateCallProof.EvidenceKey),
                    value => _method.PutExtraData(X64StackAggregateCallProof.EvidenceKey, value!), null); break;
                case "changed-target": ChangeOperand(undo, call, 0, _method); break;
                case "changed-argument":
                    var index = call.OpCode == ISIL.OpCode.CallVoid ? 1 : 2;
                    ChangeOperand(undo, call, index, new ManagedRegister(null, "changed_argument")); break;
                case "suppressed-effect":
                    // Analysis removes inert NOPs. Reintroduce an observable result
                    // write at an originally suppressed packed-copy address instead
                    // of assuming an erased instruction still exists in the graph.
                    Assert.That(call.OpCode, Is.EqualTo(ISIL.OpCode.Call));
                    var block = _method.ControlFlowGraph.Blocks.Single(block => block.Instructions.Contains(call));
                    var effect = new ManagedInstruction(_method.ControlFlowGraph.Instructions.Max(instruction => instruction.Index) + 1,
                        ISIL.OpCode.Move, call.Operands[1], new ISIL.Immediate(0))
                    {
                        NativeAddress = site.Copy.ReplacedAddresses.First(address => address != site.Copy.FirstRead)
                    };
                    undo.Push(() => block.Instructions.Remove(effect));
                    block.Instructions.Insert(block.Instructions.IndexOf(call) + 1, effect); break;
            }
            Assert.That(X64StackAggregateCallProof.HasEvidence(_method), Is.True);
            RejectPreservingBody();
        }
        finally { Restore(undo); }
    }

    private void RejectPreservingBody()
    {
        var output = _method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        var previous = output.CilMethodBody;
        Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(_method, output));
        Assert.That(output.CilMethodBody, Is.SameAs(previous));
    }

    private void AddAlias(Stack<Action> undo, MethodAnalysisContext method)
    {
        var aliases = _app.MethodsByAddress[method.UnderlyingPointer];
        undo.Push(() => aliases.RemoveAt(aliases.Count - 1)); aliases.Add(method);
    }

    private void ChangePointer(Stack<Action> undo, MethodAnalysisContext method)
    {
        var module = method.DeclaringType!.DeclaringAssembly.CodeGenModule!;
        var pointers = _app.Binary.GetCodegenModuleMethodPointers(_app.Binary.GetCodegenModuleIndex(module));
        var ordinal = checked((int)(method.Definition!.token & 0xFFFFFF) - 1);
        Change(undo, () => pointers[ordinal], value => pointers[ordinal] = value, pointers[ordinal] + 1);
    }

    private void AddUnlistedNativeBinding(Stack<Action> undo, ulong pointer)
    {
        // Keep the mutable alias cache untouched: the independent native table
        // must detect a second or interior entry even without a cached identity.
        var other = _app.SystemTypes.SystemObjectType.Methods.First(method => method.UnderlyingPointer != 0);
        var module = other.DeclaringType!.DeclaringAssembly.CodeGenModule!;
        var pointers = _app.Binary.GetCodegenModuleMethodPointers(_app.Binary.GetCodegenModuleIndex(module));
        var ordinal = checked((int)(other.Definition!.token & 0xFFFFFF) - 1);
        Change(undo, () => pointers[ordinal], value => pointers[ordinal] = value, pointer);
        WriteNative(undo, module.methodPointers + (ulong)ordinal * sizeof(ulong), BitConverter.GetBytes(pointer));
    }

    private void ChangeUnrelatedModuleSlot(Stack<Action> undo, bool native)
    {
        // An unchanged selected method and alias cache cannot excuse a disagreement
        // elsewhere in the original pointer inventory used to prove uniqueness.
        var other = _app.SystemTypes.SystemObjectType.Methods.First(method => method.UnderlyingPointer != 0);
        var module = other.DeclaringType!.DeclaringAssembly.CodeGenModule!;
        var pointers = _app.Binary.GetCodegenModuleMethodPointers(_app.Binary.GetCodegenModuleIndex(module));
        var ordinal = checked((int)(other.Definition!.token & 0xFFFFFF) - 1);
        var changed = pointers[ordinal] ^ 1UL;
        if (native) WriteNative(undo, module.methodPointers + (ulong)ordinal * sizeof(ulong), BitConverter.GetBytes(changed));
        else Change(undo, () => pointers[ordinal], value => pointers[ordinal] = value, changed);
    }

    private void WriteNative(Stack<Action> undo, ulong address, byte[] value)
    {
        var pe = (PE)_app.Binary;
        Assert.That(pe.BaseStream, Is.InstanceOf<MemoryStream>(), "Native controls must never write an original player file on disk.");
        var offset = pe.MapVirtualAddressToRaw(address, false);
        var original = pe.GetRawBinaryContent().Slice(checked((int)offset), value.Length).ToArray();
        void Write(byte[] bytes)
        {
            var position = pe.BaseStream.Position;
            try { pe.BaseStream.Position = offset; pe.BaseStream.Write(bytes); }
            finally { pe.BaseStream.Position = position; }
        }
        undo.Push(() => Write(original)); Write(value);
    }

    private static void Change<T>(Stack<Action> undo, Func<T> read, Action<T> write, T value)
    {
        var original = read(); undo.Push(() => write(original)); write(value);
    }

    private static void ChangeOperand(Stack<Action> undo, ManagedInstruction instruction, int index, ISIL.IOperand value)
    {
        var original = instruction.Operands.ToList();
        undo.Push(() => instruction.SetOperands(original));
        var changed = original.ToList(); changed[index] = value; instruction.SetOperands(changed);
    }

    private static void Restore(Stack<Action> undo)
    {
        var failures = new List<Exception>();
        while (undo.Count != 0)
            try { undo.Pop()(); } catch (Exception exception) { failures.Add(exception); }
        if (failures.Count != 0) throw new AggregateException("Aggregate mutation restoration failed.", failures);
    }
}
