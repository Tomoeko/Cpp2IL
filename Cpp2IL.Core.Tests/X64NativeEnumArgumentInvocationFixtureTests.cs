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
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64NativeEnumArgumentInvocationFixtureTests
{
    private ApplicationAnalysisContext _app = null!;
    private TypeAnalysisContext _enum = null!;
    private MethodAnalysisContext _target = null!;
    private MethodAnalysisContext[] _callers = null!;

    [OneTimeSetUp]
    public void LoadOriginalPlayer()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_ENUM_ARGUMENT_INVOCATION_FIXTURE");
        if (string.IsNullOrEmpty(input)) Assert.Ignore("Configure the neutral enum argument player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
            Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        _app = Cpp2IlApi.CurrentAppContext!;
        Assert.That(((PE)_app.Binary).BaseStream, Is.InstanceOf<MemoryStream>());
        var types = _app.GetAssemblyByName("NativeEnumArgumentInvocationFixture")!.Types
            .Where(type => type.Name != "<Module>").ToArray();
        Assert.That(types.Length, Is.EqualTo(3));
        Assert.That(types.Sum(type => type.Methods.Count), Is.EqualTo(8), "The sink and constructor stay in the full scope.");
        Assert.That(types.Sum(type => type.Fields.Count), Is.EqualTo(6));
        _enum = types.Single(type => type.Name == "Selector");
        _target = types.Single(type => type.Name == "Sink").Methods.Single(method => method.Name == "Accept");
        _callers = types.Single(type => type.Name == "Callers").Methods.ToArray();
        Assert.That(_callers.Length, Is.EqualTo(6));
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(_app);
        foreach (var method in _callers) method.Analyze();
    }

    [OneTimeTearDown]
    public void ReleasePlayer() => Cpp2IlApi.ResetInternalState();

    [TestCase("Zero")]
    [TestCase("Positive")]
    [TestCase("Negative")]
    [TestCase("Unnamed")]
    [TestCase("Forward")]
    [TestCase("Sequence")]
    public void LiteralForwardedAndSequentialArgumentsKeepTheOriginalEnumAndNullTiming(string name)
    {
        Assert.That(OriginalEnumQualified(), Is.True);
        var caller = _callers.Single(method => method.Name == name);
        RequireValidBaseline(caller);
        var output = caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(caller, output), caller.Name);
        var calls = output.CilMethodBody!.Instructions.Where(instruction =>
            instruction.OpCode == CilOpCodes.Callvirt || instruction.OpCode == CilOpCodes.Call).ToArray();
        Assert.That(calls.Length, Is.EqualTo(caller.Name == "Sequence" ? 3 : 1), caller.Name);
        var target = _target.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        foreach (var call in calls) Assert.That(call.Operand, Is.SameAs(target));
        // The native third call reuses first after its earlier null check. Its
        // direct tail invocation keeps all three effects in the original order.
        Assert.That(calls.Select(instruction => instruction.OpCode), Is.EqualTo(caller.Name == "Sequence"
            ? new[] { CilOpCodes.Callvirt, CilOpCodes.Callvirt, CilOpCodes.Call }
            : new[] { CilOpCodes.Callvirt }), caller.Name);
        Assert.That(_target.Parameters[0].ParameterType, Is.SameAs(_enum));
    }

    [TestCase("underlying")]
    [TestCase("backing-type")]
    [TestCase("backing-offset")]
    [TestCase("literal-value")]
    [TestCase("literal-row")]
    [TestCase("literal-attributes")]
    [TestCase("field-order")]
    [TestCase("raw-underlying-kind")]
    [TestCase("raw-backing-descriptor")]
    [TestCase("native-backing-offset")]
    [TestCase("field-metadata-row")]
    public void ChangedOriginalEnumRejectsFreshQualificationAndSavedInvocation(string defect)
    {
        var caller = _callers.Single(method => method.Name == "Sequence");
        RequireValidBaseline(caller);
        var backing = _enum.Fields.Single(field => !field.IsStatic);
        var literal = _enum.Fields.Single(field => field.Name == "Positive");
        var undo = new Stack<Action>();
        try
        {
            switch (defect)
            {
                case "underlying": Change(undo, () => _enum.OverrideEnumUnderlyingType, value => _enum.OverrideEnumUnderlyingType = value, _app.SystemTypes.SystemUInt32Type); break;
                case "backing-type": Change(undo, () => backing.OverrideFieldType, value => backing.OverrideFieldType = value, _app.SystemTypes.SystemUInt32Type); break;
                case "backing-offset": Change<int?>(undo, () => backing.OverrideOffset, value => backing.OverrideOffset = value, 4); break;
                case "literal-value":
                    Change(undo, () => literal.UseOverrideConstantValue, value => literal.UseOverrideConstantValue = value, true);
                    Change<object?>(undo, () => literal.OverrideConstantValue, value => literal.OverrideConstantValue = value, 18); break;
                case "literal-row":
                    var constant = literal.BackingData!.Field.DefaultValue!;
                    Change(undo, () => constant.dataIndex, value => constant.dataIndex = value,
                        LibCpp2IL.Metadata.Il2CppVariableWidthIndex<LibCpp2IL.Metadata.Il2CppDefaultValueDataDummy>.MakeTemporaryForFixedWidthUsage(constant.dataIndex.Value + 1)); break;
                case "literal-attributes": Change<FieldAttributes?>(undo, () => literal.OverrideAttributes, value => literal.OverrideAttributes = value,
                    literal.Attributes & ~FieldAttributes.Literal); break;
                case "field-order":
                    var first = _enum.Fields[0]; var second = _enum.Fields[1];
                    _enum.Fields[0] = second; _enum.Fields[1] = first;
                    undo.Push(() => { _enum.Fields[0] = first; _enum.Fields[1] = second; }); break;
                case "raw-underlying-kind":
                    var underlying = _enum.Definition!.EnumUnderlyingType!;
                    Change(undo, () => underlying.Type, value => underlying.Type = value, Il2CppTypeEnum.IL2CPP_TYPE_U4); break;
                case "raw-backing-descriptor":
                    var raw = backing.BackingData!.Field.RawFieldType!;
                    Assert.That(_app.Binary.TryGetTypeVirtualAddress(raw, out var address), Is.True);
                    MutateNative(undo, address + 8, 1); break;
                case "native-backing-offset":
                    Assert.That(_app.Binary.TryGetGenericMethodTableRegistration(out var registration), Is.True);
                    var metadataRegistration = _app.Binary.ReadReadableAtVirtualAddress<Il2CppMetadataRegistration>(registration.MetadataRegistrationAddress);
                    var offsets = _app.Binary.ReadPointerAtVirtualAddress(metadataRegistration.fieldOffsetListAddress +
                        (ulong)_enum.Definition!.TypeIndex.Value * sizeof(ulong));
                    WriteNative(undo, offsets, BitConverter.GetBytes(17)); break;
                case "field-metadata-row":
                    MutateMetadata(undo, _app.Metadata.metadataHeader.fields.Offset +
                        (long)backing.BackingData!.Field.FieldIndex.Value * 12 + 8, 1); break;
            }
            Assert.That(OriginalEnumQualified(), Is.False, "Initial admission must independently reject the changed original.");
            Reject(caller);
        }
        finally { Restore(undo); }
        Assert.That(OriginalEnumQualified(), Is.True);
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True);
    }

    [TestCase("target-parameter")]
    [TestCase("forwarded-parameter")]
    [TestCase("argument-literal")]
    [TestCase("argument-width")]
    [TestCase("native-body")]
    [TestCase("constant-data")]
    [TestCase("evidence-removed")]
    public void ChangedSignatureBitsOrRetainedValuesCannotReuseTheInvocation(string defect)
    {
        var caller = _callers.Single(method => method.Name == (defect == "forwarded-parameter" ? "Forward" : "Sequence"));
        RequireValidBaseline(caller);
        var call = caller.ControlFlowGraph!.Instructions.First(instruction => instruction.IsCall &&
            ReferenceEquals(instruction.Operands[0], _target));
        var start = call.OpCode == OpCode.Call ? 3 : 2;
        var undo = new Stack<Action>();
        try
        {
            switch (defect)
            {
                case "target-parameter": Change(undo, () => _target.Parameters[0].OverrideParameterType,
                    value => _target.Parameters[0].OverrideParameterType = value, _app.SystemTypes.SystemInt32Type); break;
                case "forwarded-parameter": Change(undo, () => caller.Parameters[1].OverrideParameterType,
                    value => caller.Parameters[1].OverrideParameterType = value, _app.SystemTypes.SystemUInt32Type); break;
                case "argument-literal": Change(undo, () => call.Operands[start], value => call.SetOperand(start, value), (IOperand)new Immediate(18)); break;
                case "argument-width": Change(undo, () => call.IntegerBitWidth, value => call.IntegerBitWidth = value, 32); break;
                case "native-body": MutateNative(undo, caller.UnderlyingPointer, 1); break;
                case "constant-data":
                    var constant = _enum.Fields.Single(field => field.Name == "Positive").BackingData!.Field.DefaultValue!;
                    MutateMetadata(undo, _app.Metadata.metadataHeader.fieldAndParameterDefaultValueData.Offset + constant.dataIndex.Value, 2); break;
                case "evidence-removed": Change(undo, () => caller.GetExtraData<object>(X64NativeNullCheckedInvocationProof.EvidenceKey),
                    value => caller.PutExtraData(X64NativeNullCheckedInvocationProof.EvidenceKey, value!), null); break;
            }
            Reject(caller);
        }
        finally { Restore(undo); }
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True);
    }

    [TestCase("output-underlying")]
    [TestCase("output-literal")]
    [TestCase("output-field-order")]
    [TestCase("output-parameter")]
    [TestCase("output-layout")]
    public void ChangedOutputCannotRetypeTheOriginalEnumArgument(string defect)
    {
        var caller = _callers.Single(method => method.Name == "Sequence");
        RequireValidBaseline(caller);
        var declaration = _enum.GetExtraData<TypeDefinition>("AsmResolverType")!;
        var backing = declaration.Fields.Single(field => field.Name == "value__");
        var literal = declaration.Fields.Single(field => field.Name == "Positive");
        var target = _target.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        var undo = new Stack<Action>();
        try
        {
            switch (defect)
            {
                case "output-underlying": Change(undo, () => backing.Signature, value => backing.Signature = value,
                    new FieldSignature(declaration.DeclaringModule!.CorLibTypeFactory.UInt32)); break;
                case "output-literal": Change(undo, () => literal.Constant, value => literal.Constant = value, Constant.FromValue(18)); break;
                case "output-field-order":
                    var originalFields = declaration.Fields.ToArray();
                    declaration.Fields.Clear();
                    foreach (var field in originalFields.Reverse()) declaration.Fields.Add(field);
                    undo.Push(() => { declaration.Fields.Clear(); foreach (var field in originalFields) declaration.Fields.Add(field); }); break;
                case "output-parameter": Change(undo, () => target.Signature!.ParameterTypes[0], value => target.Signature!.ParameterTypes[0] = value,
                    (TypeSignature)declaration.DeclaringModule!.CorLibTypeFactory.Int32); break;
                case "output-layout": Change(undo, () => declaration.ClassLayout, value => declaration.ClassLayout = value, new ClassLayout(1, 4)); break;
            }
            Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True, "The independent output mutation leaves native/model evidence intact.");
            Assert.That(X64NativeNullCheckedInvocationProof.EnumOutputDeclarationsValid(caller), Is.False);
            Assert.That(() => IlGenerator.GenerateIl(caller, caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!),
                Throws.TypeOf<DecompilerException>());
        }
        finally { Restore(undo); }
        Assert.That(X64NativeNullCheckedInvocationProof.EnumOutputDeclarationsValid(caller), Is.True);
    }

    [TestCase("setup-width")]
    [TestCase("nonzero-origin")]
    [TestCase("different-enum")]
    public void ChangedTypedSetupCannotDeleteTheOriginalRuntimeNullArm(string defect)
    {
        var caller = _callers.Single(method => method.Name == "Positive");
        RequireValidBaseline(caller);
        try
        {
            caller.ReleaseAnalysisData();
            PrepareSsa(caller);
            var setup = caller.ControlFlowGraph!.Instructions.Single(instruction =>
                instruction is { OpCode: OpCode.Add, IntegerBitWidth: 32,
                    Operands: [LocalVariable destination, Immediate { Value: 0 }, Immediate { Value: 17 }] } &&
                ReferenceEquals(destination.Type, _enum));
            Assert.That(X64NativeNullCheckedInvocationProof.IsEnumArgumentSetup(caller, setup), Is.True);
            Assert.That(caller.ControlFlowGraph.Instructions.Count(instruction => instruction.OpCode == OpCode.RuntimeNullThrow), Is.EqualTo(1));
            switch (defect)
            {
                case "setup-width": setup.IntegerBitWidth = 64; break;
                case "nonzero-origin": setup.SetOperand(1, new Immediate(1)); break;
                case "different-enum":
                    var other = _app.AllTypes.Single(type => type.FullName == "System.TypeCode");
                    Assert.That(X64NativeNullCheckedInvocationProof.IsSignedEnumArgumentType(other), Is.True);
                    ((LocalVariable)setup.Operands[0]).Type = other;
                    break;
            }
            Assert.That(RuntimeNullGuardCoalescer.Run(caller), Is.Zero);
            Assert.That(caller.ControlFlowGraph.Instructions.Count(instruction => instruction.OpCode == OpCode.RuntimeNullThrow), Is.EqualTo(1));
            Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.False);
        }
        finally
        {
            caller.ReleaseAnalysisData();
            caller.Analyze();
        }
        RequireValidBaseline(caller);
    }

    private static void RequireValidBaseline(MethodAnalysisContext caller)
    {
        Assert.That(X64NativeNullCheckedInvocationProof.HasEvidence(caller), Is.True, caller.Name);
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.True, caller.Name);
        Assert.That(X64NativeNullCheckedInvocationProof.EnumOutputDeclarationsValid(caller), Is.True, caller.Name);
    }

    private static void PrepareSsa(MethodAnalysisContext method)
    {
        method.EnsureRawBytes();
        method.ConvertedIsil = method.AppContext.InstructionSet.GetIsilFromMethod(method);
        method.ParameterOperands = method.AppContext.InstructionSet.GetParameterOperandsFromMethod(method);
        method.ControlFlowGraph = new ISILControlFlowGraph(method.ConvertedIsil);
        StackAnalyzer.Analyze(method);
        method.DominatorInfo = new DominatorInfo(method.ControlFlowGraph);
        SsaForm.Build(method);
        LocalVariables.CreateAll(method);
        FlagConditionRecovery.Run(method);
        DeadCodeEliminator.Run(method);
        MetadataResolver.ResolveAll(method);
        KeyFunctionRecovery.Run(method);
        DeadCodeEliminator.Run(method);
        WriteBarrierRecovery.Run(method);
        InterfaceDispatchRecovery.Run(method);
        LocalVariables.ResolveTypesAndFields(method);
        IntegerTruncationRecovery.Run(method);
        DelegateInvokeRecovery.Run(method);
        BooleanFlagSimplifier.Run(method);
        DeadCodeEliminator.Run(method);
        SsaSimplifier.Run(method);
        for (var index = 0; index < 8 && ConstantFolder.Run(method); index++) SsaSimplifier.Run(method);
        ArrayLengthReadRecovery.Run(method);
        LocalVariables.PropagateLateSignedIntegerTypes(method);
        MetadataResolver.ResolveProvedInertObjectConstructorTailCalls(method);
        InternalCallGuardRemover.Run(method);
        KeyFunctionRecovery.Run(method);
    }

    private bool OriginalEnumQualified() => (bool)typeof(X64NativeNullCheckedInvocationProof)
        .GetMethod("TryEnumDeclaration", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [_enum, null])!;

    private static void Reject(MethodAnalysisContext caller)
    {
        Assert.That(X64NativeNullCheckedInvocationProof.IsValidFor(caller), Is.False);
        Assert.That(() => IlGenerator.GenerateIl(caller, caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!),
            Throws.TypeOf<DecompilerException>());
    }

    private void MutateNative(Stack<Action> undo, ulong address, byte mask)
    {
        var offset = checked((int)_app.Binary.MapVirtualAddressToRaw(address, false));
        WriteNative(undo, address, [(byte)(_app.Binary.GetRawBinaryContent()[offset] ^ mask)]);
    }

    private void WriteNative(Stack<Action> undo, ulong address, byte[] changed)
    {
        var offset = _app.Binary.MapVirtualAddressToRaw(address, false);
        Write(undo, ((PE)_app.Binary).BaseStream, offset, changed);
    }

    private void MutateMetadata(Stack<Action> undo, long offset, byte mask)
    {
        var original = _app.Metadata.ReadByteArrayAtRawAddress(offset, 1)[0];
        Write(undo, _app.Metadata.BaseStream, offset, [(byte)(original ^ mask)]);
    }

    private static void Write(Stack<Action> undo, Stream stream, long offset, byte[] changed)
    {
        var position = stream.Position;
        var original = new byte[changed.Length]; stream.Position = offset; stream.ReadExactly(original);
        stream.Position = offset; stream.Write(changed); stream.Position = position;
        undo.Push(() => { var current = stream.Position; stream.Position = offset; stream.Write(original); stream.Position = current; });
    }

    private static void Change<T>(Stack<Action> undo, Func<T> read, Action<T> write, T value)
    {
        var previous = read(); undo.Push(() => write(previous)); write(value);
    }

    private static void Restore(Stack<Action> undo) { while (undo.TryPop(out var restore)) restore(); }
}
