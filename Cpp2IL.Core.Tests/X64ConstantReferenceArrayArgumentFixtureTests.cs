using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64ConstantReferenceArrayArgumentFixtureTests
{
    private MethodAnalysisContext _method = null!;
    private MethodAnalysisContext _target = null!;
    private Iced.Intel.Instruction[] _body = null!;

    [OneTimeSetUp]
    public void LoadFixture()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_CONSTANT_REFERENCE_ARRAY_ARGUMENT_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_CONSTANT_REFERENCE_ARRAY_ARGUMENT_FIXTURE_INPUT to the neutral player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
            Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var assembly = Cpp2IlApi.CurrentAppContext!.Assemblies.Single(candidate =>
            candidate.Name == "NativeConstantReferenceArrayArgumentFixture");
        var methods = assembly.Types.SelectMany(type => type.Methods).ToArray();
        Assert.That(methods, Has.Length.EqualTo(4));
        _method = methods.Single(method => method.Name == "Forward");
        _target = methods.Single(method => method.Name == "Capture");
        _method.EnsureRawBytes();
        _body = X64NativeInstructionReader.ReadRootBody(_method)!;
        Assert.That(Find(), Is.Not.Null);
        _method.Analyze();
        Assert.That(_method.GetExtraData<X64GuardedArrayOperationProof.Evidence>(
            X64GuardedArrayOperationProof.EvidenceKey), Is.Not.Null);
        IlGenerator.ValidateGuardedArrayOperations(_method);
    }

    [OneTimeTearDown]
    public void ReleaseFixture() => Cpp2IlApi.ResetInternalState();

    [TestCase("array-bits")]
    [TestCase("array-union")]
    [TestCase("element-high-index")]
    [TestCase("element-value-type")]
    [TestCase("element-bits")]
    [TestCase("parameter-high-index")]
    [TestCase("owner-base-high-index")]
    public void InitialMalformedDescriptorCannotResolveThroughCachedTypes(string mutation)
    {
        var array = _method.DeclaringType!.Fields.Single(field => field.Name == "Items").BackingData!.Field.RawFieldType!;
        var raw = mutation.StartsWith("element", StringComparison.Ordinal) ? array.GetEncapsulatedType() :
            mutation.StartsWith("parameter", StringComparison.Ordinal) ? _target.Parameters[0].Definition!.RawType! :
            mutation.StartsWith("owner-base", StringComparison.Ordinal) ? _method.DeclaringType.Definition!.RawBaseType! : array;
        var bits = raw.Bits;
        var data = raw.Datapoint;
        var dummy = raw.Data.Dummy;
        var valueType = raw.ValueType;
        try
        {
            switch (mutation)
            {
                case "array-bits": case "element-bits": raw.Bits ^= 1U << 16; break;
                case "array-union": raw.Datapoint++; break;
                case "element-value-type": raw.ValueType = 1; raw.Bits |= 1U << 31; break;
                default: raw.Datapoint += 1UL << 32; raw.Data.Dummy = raw.Datapoint; break;
            }
            Assert.That(Find(), Is.Null, mutation);
            Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(_method));
        }
        finally
        {
            raw.Bits = bits; raw.Datapoint = data; raw.Data.Dummy = dummy; raw.ValueType = valueType;
        }
        Assert.That(Find(), Is.Not.Null);
        IlGenerator.ValidateGuardedArrayOperations(_method);
    }

    [Test]
    public void DuplicateNativeTargetIsRejectedBeforeEligibility()
    {
        var bindings = _method.AppContext.MethodsByAddress[_target.UnderlyingPointer];
        bindings.Add(_method);
        try { Assert.That(Find(), Is.Null); }
        finally { bindings.RemoveAt(bindings.Count - 1); }
        Assert.That(Find(), Is.Not.Null);
    }

    [Test]
    public void OriginalMemberIdentityRemainsFrozen()
    {
        var definition = _target.Definition!;
        var token = definition.token;
        try
        {
            definition.token++;
            Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(_method));
        }
        finally { definition.token = token; }
        IlGenerator.ValidateGuardedArrayOperations(_method);
    }

    [TestCase("constructor-name")]
    [TestCase("constructor-flags")]
    [TestCase("constructor-implementation")]
    [TestCase("removed-member")]
    [TestCase("duplicate-member")]
    [TestCase("foreign-member")]
    [TestCase("field-type")]
    [TestCase("constructor-raw-static")]
    [TestCase("element-constructor-raw-static")]
    public void InitialOwnerClosureCannotInventInitializationOrMembers(string mutation)
    {
        var owner = _method.DeclaringType!;
        var constructorOwner = mutation == "element-constructor-raw-static"
            ? _target.Parameters[0].ParameterType : owner;
        var constructor = constructorOwner.Methods.Single(method => method.Name == ".ctor");
        var neighbor = owner.Fields.Single(field => field.Name == "Value");
        var originalMethods = owner.Methods.ToArray();
        var name = constructor.Name;
        var attributes = constructor.Attributes;
        var implementation = constructor.ImplAttributes;
        var fieldType = neighbor.OverrideFieldType;
        var rawFlags = constructor.Definition!.flags;
        try
        {
            switch (mutation)
            {
                case "constructor-name": constructor.Name = ".cctor"; break;
                case "constructor-flags": constructor.Attributes |= MethodAttributes.Static; break;
                case "constructor-implementation": constructor.ImplAttributes |= MethodImplAttributes.Synchronized; break;
                case "removed-member": owner.Methods.Remove(constructor); break;
                case "duplicate-member": owner.Methods.Add(constructor); break;
                case "foreign-member": owner.Methods.Add(_method.AppContext.SystemTypes.SystemObjectType.Methods[0]); break;
                case "field-type": neighbor.OverrideFieldType = _method.AppContext.SystemTypes.SystemObjectType; break;
                case "constructor-raw-static": case "element-constructor-raw-static":
                    constructor.Definition.flags |= (ushort)MethodAttributes.Static; break;
                default: throw new ArgumentException(mutation);
            }
            Assert.That(Find(), Is.Null, mutation);
        }
        finally
        {
            constructor.Name = name; constructor.Attributes = attributes; constructor.ImplAttributes = implementation;
            neighbor.OverrideFieldType = fieldType;
            constructor.Definition.flags = rawFlags;
            owner.Methods.Clear(); owner.Methods.AddRange(originalMethods);
        }
        Assert.That(Find(), Is.Not.Null);
        IlGenerator.ValidateGuardedArrayOperations(_method);
    }

    [TestCase(-1)]
    [TestCase(1)]
    [TestCase(int.MaxValue)]
    public void FinalGraphMustRetainTheExactZeroIndex(int value)
    {
        var access = _method.ControlFlowGraph!.Instructions.SelectMany(instruction => instruction.Operands)
            .OfType<ArrayAccess>().Single();
        var original = access.Index;
        Assert.That(original, Is.EqualTo(new Immediate(0)));
        try
        {
            access.Index = new Immediate(value);
            Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(_method));
        }
        finally { access.Index = original; }
        IlGenerator.ValidateGuardedArrayOperations(_method);
    }

    [Test]
    public void CallCannotReplaceCapturedElementWithNull()
    {
        var call = _method.ControlFlowGraph!.Instructions.Single(instruction => instruction.IsCall);
        var index = call.OpCode == OpCode.Call ? 3 : 2;
        var original = call.Operands[index];
        try
        {
            call.SetOperand(index, new Immediate(0));
            Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(_method));
        }
        finally { call.SetOperand(index, original); }
        IlGenerator.ValidateGuardedArrayOperations(_method);
    }

    [TestCase(8)]
    [TestCase(32)]
    [TestCase(64)]
    public void ReferenceReadCannotAcquireAnIntegerWidth(int width)
    {
        var read = _method.ControlFlowGraph!.Instructions.Single(instruction =>
            instruction.Operands is [LocalVariable, ArrayAccess]);
        var original = read.IntegerBitWidth;
        Assert.That(original, Is.Zero);
        try
        {
            read.IntegerBitWidth = width;
            Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(_method));
        }
        finally { read.IntegerBitWidth = original; }
        IlGenerator.ValidateGuardedArrayOperations(_method);
    }

    [TestCase(false, "return")]
    [TestCase(true, "return")]
    [TestCase(false, "virtual")]
    [TestCase(true, "virtual")]
    [TestCase(false, "abstract")]
    [TestCase(true, "abstract")]
    [TestCase(false, "synchronized")]
    [TestCase(true, "synchronized")]
    public void OriginalConstructorMustKeepItsInstanceVoidSemantics(bool element, string mutation)
    {
        var owner = element ? _target.Parameters[0].ParameterType : _method.DeclaringType!;
        var definition = owner.Methods.Single(method => method.Name == ".ctor").Definition!;
        var flags = definition.flags;
        var implementation = definition.iflags;
        var returnIndex = definition.returnTypeIdx;
        try
        {
            switch (mutation)
            {
                case "return": definition.returnTypeIdx = _method.AppContext.SystemTypes.SystemInt32Type.Definition!.ByvalTypeIndex; break;
                case "virtual": definition.flags |= (ushort)MethodAttributes.Virtual; break;
                case "abstract": definition.flags |= (ushort)MethodAttributes.Abstract; break;
                case "synchronized": definition.iflags |= (ushort)MethodImplAttributes.Synchronized; break;
                default: throw new ArgumentException(mutation);
            }
            Assert.That(Find(), Is.Null, mutation);
        }
        finally { definition.flags = flags; definition.iflags = implementation; definition.returnTypeIdx = returnIndex; }
        Assert.That(Find(), Is.Not.Null);
        IlGenerator.ValidateGuardedArrayOperations(_method);
    }

    private X64GuardedArrayOperationProof.Evidence? Find() => X64GuardedArrayOperationProof.Find(_method, _body);
}
