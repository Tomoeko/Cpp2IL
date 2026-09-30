using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Reporting;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64ScalarWordWrapperConversionFixtureTests
{
    private TypeAnalysisContext _type = null!;
    private MethodAnalysisContext _construct = null!, _project = null!, _initializer = null!;

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_SCALAR_WORD_WRAPPER_CONVERSION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_SCALAR_WORD_WRAPPER_CONVERSION_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _type = app.GetAssemblyByName("ScalarWordWrapperConversionFixture")!.Types.Single(type => type.Name == "WordCell");
        Assert.That(_type.Methods, Has.Count.EqualTo(3));
        Assert.That(_type.Fields, Has.Count.EqualTo(2));
        _construct = _type.Methods.Single(method => ReferenceEquals(method.ReturnType, _type));
        _project = _type.Methods.Single(method => ReferenceEquals(method.ReturnType, app.SystemTypes.SystemInt16Type));
        _initializer = _type.Methods.Single(method => method.Name == ".cctor");
        _project.Analyze();
        Accept();
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void TheCompleteWordScopeKeepsSignedFieldsAndRegisterAggregateIdentity()
    {
        Accept();
        var construct = X64ScalarWrapperConversionProof.Find(_construct)!;
        var project = X64SmallAggregateFieldGetterProof.GetEvidence(_project)!;
        Assert.That(construct.Native.Width, Is.EqualTo(16));
        Assert.That(construct.Native.Signed, Is.False);
        Assert.That(project.Widen, Is.False, "The narrow managed return interprets its own signed low16 bits.");
        Assert.That(construct.Field, Is.SameAs(project.Field));
        Assert.That(construct.Field.FieldType, Is.SameAs(_construct.AppContext.SystemTypes.SystemInt16Type));
        Assert.That(_construct.AppContext.MethodsByAddress[_construct.UnderlyingPointer], Does.Contain(_construct));
        Assert.That(_project.AppContext.MethodsByAddress[_project.UnderlyingPointer], Does.Contain(_project));
        var body = Definition(_construct).CilMethodBody!;
        Assert.That(body.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Initobj), Is.EqualTo(1));
        Assert.That(body.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Stfld), Is.EqualTo(1));
        Assert.That(body.Instructions.Any(instruction => instruction.OpCode == CilOpCodes.Newobj), Is.False);
        Assert.That(Definition(_project).CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld),
            Is.EqualTo(1));
    }

    [Test]
    public void TheNegativeWordInitializerUsesAnInt32StackConstantAndKeepsBeforeFieldInit()
    {
        Accept();
        var proof = X64ScalarWrapperStaticConstructorProof.Find(_initializer)!;
        Assert.That(proof.ValueBits, Is.EqualTo(unchecked((ushort)-17)));
        Assert.That((_type.Attributes & TypeAttributes.BeforeFieldInit) != 0, Is.True);
        var body = Definition(_initializer).CilMethodBody!;
        Assert.That(body.Instructions.Single(instruction => instruction.OpCode == CilOpCodes.Ldc_I4).Operand,
            Is.EqualTo(-17));
        Assert.That(body.Instructions.Any(instruction => instruction.OpCode == CilOpCodes.Ldc_I8), Is.False);
        Assert.That(body.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Stsfld), Is.EqualTo(1));
        Assert.That(X64ScalarWrapperTailCallProof.ScalarField(_type), Is.Null,
            "Word storage must not widen the existing UInt32/UInt64 tail-call family.");
    }

    [TestCase("before-field-init")]
    [TestCase("missing-initializer-flag")]
    [TestCase("static-offset")]
    [TestCase("static-type")]
    [TestCase("scalar-offset")]
    [TestCase("scalar-unsigned")]
    [TestCase("initializer-flags")]
    [TestCase("initializer-token")]
    [TestCase("initializer-return-attributes")]
    [TestCase("initializer-native-value")]
    [TestCase("initializer-excluded")]
    public void BothConversionsRequireTheUnchangedCompleteInitializerAndWordLayout(string mutation)
    {
        Accept();
        var field = _type.Fields.Single(candidate => !candidate.IsStatic);
        var seed = _type.Fields.Single(candidate => candidate.IsStatic);
        var flags = _type.Definition!.Flags;
        var bits = _type.Definition.Bitfield;
        var constructorFlags = _initializer.Definition!.flags;
        var token = _initializer.Definition.token;
        var rawReturn = _initializer.Definition.RawReturnType!;
        var returnAttributes = rawReturn.Attrs;
        var limit = MethodAnalysisContext.MaxMethodSizeBytes;
        var pe = (PE)_construct.AppContext.Binary;
        var streamPosition = pe.BaseStream.Position;
        long changedOffset = -1;
        byte originalByte = 0;
        try
        {
            switch (mutation)
            {
                case "before-field-init": _type.Definition.Flags ^= (uint)TypeAttributes.BeforeFieldInit; break;
                case "missing-initializer-flag": _type.Definition.Bitfield &= ~(1U << 3); break;
                case "static-offset": seed.OverrideOffset = 2; break;
                case "static-type": seed.OverrideFieldType = _construct.AppContext.SystemTypes.SystemInt16Type; break;
                case "scalar-offset": field.OverrideOffset = 1; break;
                case "scalar-unsigned": field.OverrideFieldType = _construct.AppContext.SystemTypes.SystemUInt16Type; break;
                case "initializer-flags": _initializer.Definition.flags ^= (ushort)MethodAttributes.HideBySig; break;
                case "initializer-token": _initializer.Definition.token++; break;
                case "initializer-return-attributes": rawReturn.Attrs ^= (uint)ParameterAttributes.In; break;
                case "initializer-native-value":
                    var literal = X86Utils.Iterate(_initializer).Single(native => native.Code == Iced.Intel.Code.Mov_r32_imm32);
                    changedOffset = pe.MapVirtualAddressToRaw(literal.NextIP - 4);
                    originalByte = pe.GetByteAtRawAddress((ulong)changedOffset);
                    pe.BaseStream.Position = changedOffset;
                    pe.BaseStream.WriteByte((byte)(originalByte ^ 1));
                    break;
                case "initializer-excluded": MethodAnalysisContext.MaxMethodSizeBytes = 16; break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            Assert.That(X64ScalarWrapperConversionRecovery.TryGenerate(_construct, Definition(_construct)), Is.False, mutation);
            Assert.That(SmallAggregateFieldGetterRecovery.IsValidFor(_project), Is.False, mutation);
            Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(_project, Definition(_project)), mutation);
            if (mutation != "initializer-excluded")
                Assert.That(X64ScalarWrapperStaticConstructorRecovery.TryGenerate(_initializer, Definition(_initializer)), Is.False, mutation);
        }
        finally
        {
            _type.Definition.Flags = flags;
            _type.Definition.Bitfield = bits;
            seed.OverrideOffset = null;
            seed.OverrideFieldType = null;
            field.OverrideOffset = null;
            field.OverrideFieldType = null;
            _initializer.Definition.flags = constructorFlags;
            _initializer.Definition.token = token;
            rawReturn.Attrs = returnAttributes;
            MethodAnalysisContext.MaxMethodSizeBytes = limit;
            if (changedOffset >= 0)
            {
                pe.BaseStream.Position = changedOffset;
                pe.BaseStream.WriteByte(originalByte);
            }
            pe.BaseStream.Position = streamPosition;
        }
        Accept();
    }

    [TestCase("producer-register")]
    [TestCase("store-register")]
    [TestCase("store-width")]
    [TestCase("intervening-clobber")]
    public void WordRegisterInitializerRequiresTheAdjacentExactLowWordProducer(string mutation)
    {
        var native = X86Utils.Iterate(_initializer).ToArray();
        Assert.That(native, Has.Length.EqualTo(12));
        Assert.That(X64ScalarWrapperStaticConstructorProof.TryProveShape(native), Is.Not.Null);
        var changed = native.ToArray();
        switch (mutation)
        {
            case "producer-register": changed[8].Op0Register = Iced.Intel.Register.EDX; break;
            case "store-register": changed[9].Op1Register = Iced.Intel.Register.DX; break;
            case "store-width": changed[9].Code = Iced.Intel.Code.Mov_rm32_r32; changed[9].Op1Register = Iced.Intel.Register.EAX; break;
            case "intervening-clobber":
                var clobber = Iced.Intel.Instruction.Create(Iced.Intel.Code.Xor_rm32_r32,
                    Iced.Intel.Register.EAX, Iced.Intel.Register.EAX);
                changed = native.Take(9).Append(clobber).Concat(native.Skip(9)).ToArray();
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Assert.That(X64ScalarWrapperStaticConstructorProof.TryProveShape(changed), Is.Null, mutation);
    }

    [TestCase("parameter-unsigned")]
    [TestCase("parameter-modifier")]
    [TestCase("return-byref")]
    [TestCase("managed-token")]
    [TestCase("lost-native-binding")]
    [TestCase("lost-admission")]
    public void ConstructionCannotReuseDifferentScalarSignaturesOrLostIdentity(string mutation)
    {
        Accept();
        var parameter = _construct.Parameters.Single();
        var rawParameter = parameter.Definition!.RawType!;
        var mods = rawParameter.NumMods;
        var rawReturn = _construct.Definition!.RawReturnType!;
        var byref = rawReturn.Byref;
        var token = _construct.Definition.token;
        var bindings = _construct.AppContext.MethodsByAddress[_construct.UnderlyingPointer].ToArray();
        var saved = _construct.GetExtraData<X64ScalarWrapperConversionProof.Proof>(X64ScalarWrapperConversionProof.EvidenceKey)!;
        try
        {
            switch (mutation)
            {
                case "parameter-unsigned": parameter.OverrideParameterType = _construct.AppContext.SystemTypes.SystemUInt16Type; break;
                case "parameter-modifier": rawParameter.NumMods = 1; break;
                case "return-byref": rawReturn.Byref = 1; break;
                case "managed-token": _construct.Definition.token++; break;
                case "lost-native-binding":
                    _construct.AppContext.MethodsByAddress[_construct.UnderlyingPointer] = bindings.Where(method => !ReferenceEquals(method, _construct)).ToList();
                    break;
                case "lost-admission": _construct.PutExtraData<X64ScalarWrapperConversionProof.Proof>(X64ScalarWrapperConversionProof.EvidenceKey, null!); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            Assert.That(X64ScalarWrapperConversionRecovery.TryGenerate(_construct, Definition(_construct)), Is.False, mutation);
        }
        finally
        {
            parameter.OverrideParameterType = null;
            rawParameter.NumMods = mods;
            rawReturn.Byref = byref;
            _construct.Definition.token = token;
            _construct.AppContext.MethodsByAddress[_construct.UnderlyingPointer] = bindings.ToList();
            _construct.PutExtraData(X64ScalarWrapperConversionProof.EvidenceKey, saved);
        }
        Accept();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OutputCannotPublishOrdinaryFallbackAfterWrapperAdmissionChanges(bool initializer)
    {
        Accept();
        var method = initializer ? _initializer : _construct;
        var definition = Definition(method);
        var token = method.Definition!.token;
        var saved = _construct.GetExtraData<X64ScalarWrapperConversionProof.Proof>(X64ScalarWrapperConversionProof.EvidenceKey)!;
        var output = new InspectableRecoveryOutput();
        output.Begin(method.AppContext);
        try
        {
            if (initializer)
                method.Definition.token++;
            else
                method.PutExtraData<X64ScalarWrapperConversionProof.Proof>(X64ScalarWrapperConversionProof.EvidenceKey, null!);

            output.Fill(definition, method);
            var result = output.Snapshot(method.AppContext).Methods.Single(item =>
                item.AssemblyName == _type.DeclaringAssembly.Name && item.Signature == method.FullNameWithSignature);
            Assert.That(result.Disposition, Is.EqualTo(MethodRecoveryDisposition.Failed));
            Assert.That(result.ManagedIlValidation, Is.EqualTo("NotRun"));
            Assert.That(result.Reasons.Any(reason => reason.Contains("evidence is missing or changed")), Is.True);
            Assert.That(definition.CilMethodBody!.Instructions.Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { CilOpCodes.Ldstr, CilOpCodes.Newobj, CilOpCodes.Throw }));
        }
        finally
        {
            method.Definition.token = token;
            _construct.PutExtraData(X64ScalarWrapperConversionProof.EvidenceKey, saved);
        }
        Accept();
    }

    private sealed class InspectableRecoveryOutput : AsmResolverDllOutputFormatIlRecovery
    {
        internal void Begin(ApplicationAnalysisContext context) => BeginRecoveryReport(context);
        internal void Fill(MethodDefinition definition, MethodAnalysisContext method) => FillMethodBody(definition, method);
        internal RecoveryReport Snapshot(ApplicationAnalysisContext context) => CreateRecoveryReport(context, true);
    }

    private void Accept()
    {
        Assert.That(X64ScalarWrapperConversionRecovery.TryGenerate(_construct, Definition(_construct)), Is.True);
        Assert.That(SmallAggregateFieldGetterRecovery.IsValidFor(_project), Is.True);
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(_project, Definition(_project)));
        Assert.That(X64ScalarWrapperStaticConstructorRecovery.TryGenerate(_initializer, Definition(_initializer)), Is.True);
    }

    private static MethodDefinition Definition(MethodAnalysisContext method) => method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
}
