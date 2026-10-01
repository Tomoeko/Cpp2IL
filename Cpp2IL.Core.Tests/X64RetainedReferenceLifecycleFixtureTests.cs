using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64RetainedReferenceLifecycleFixtureTests
{
    private MethodAnalysisContext _caller = null!;
    private MethodAnalysisContext _constructor = null!;
    private TypeAnalysisContext _ancestor = null!;
    private Iced.Intel.Instruction[] _body = null!;

    [OneTimeSetUp]
    public void LoadFixture()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_FRAMEWORK_ANCESTOR_ARRAY_ARGUMENT_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_FRAMEWORK_ANCESTOR_ARRAY_ARGUMENT_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
            Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        var owner = app.GetAssemblyByName("FrameworkAncestorArrayArgumentFixture")!.Types.Single(type => type.Name == "Cell");
        _caller = owner.Methods.Single(method => method.Name == "Forward");
        _constructor = owner.Methods.Single(method => method.Name == ".ctor");
        _ancestor = owner.BaseType!;
        _body = X64NativeInstructionReader.ReadRootBody(_caller)!;
        AcceptBoth();
    }

    [OneTimeTearDown]
    public void ReleaseFixture() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void ConstructorCallsTheOriginalImmediateBaseWithoutAddingAnInitializerCall()
    {
        var evidence = X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor)!;
        Assert.That(evidence.BaseConstructor.DeclaringType, Is.SameAs(_ancestor));
        var definition = _constructor.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
        Assert.That(X64RetainedAncestorConstructorRecovery.TryGenerate(_constructor, definition), Is.True);
        Assert.That(definition.CilMethodBody!.Instructions.Select(instruction => instruction.OpCode),
            Is.EqualTo(new[] { CilOpCodes.Ldarg_0, CilOpCodes.Call, CilOpCodes.Ret }));
        Assert.That(definition.CilMethodBody.Instructions[1].Operand,
            Is.SameAs(evidence.BaseConstructor.GetExtraData<IMethodDescriptor>("AsmResolverMethod")));
    }

    [TestCase("owner")]
    [TestCase("element")]
    [TestCase("ancestor-before-field-init")]
    [TestCase("ancestor-cctor-bit")]
    [TestCase("ancestor-cctor-static")]
    [TestCase("ancestor-cctor-synchronized")]
    [TestCase("ancestor-cctor-return")]
    [TestCase("owner-ctor-static")]
    public void OriginalLifecycleFlagsCannotBeReinterpreted(string mutation)
    {
        var owner = _caller.DeclaringType!;
        var element = ((SzArrayTypeAnalysisContext)owner.Fields.Single(field => field.Name == "Items").FieldType).ElementType;
        var type = mutation == "owner" ? owner : mutation == "element" ? element : _ancestor;
        var bitfield = type.Definition!.Bitfield;
        var typeFlags = type.Definition.Flags;
        var method = mutation == "owner-ctor-static" ? _constructor : _ancestor.Methods.Single(candidate => candidate.Name == ".cctor");
        var flags = method.Definition!.flags;
        var implementation = method.Definition.iflags;
        var returnIndex = method.Definition.returnTypeIdx;
        try
        {
            switch (mutation)
            {
                case "owner": case "element": type.Definition.Bitfield |= 1U << 3; break;
                case "ancestor-before-field-init": type.Definition.Flags &= ~(uint)TypeAttributes.BeforeFieldInit; break;
                case "ancestor-cctor-bit": type.Definition.Bitfield &= ~(1U << 3); break;
                case "ancestor-cctor-static": method.Definition.flags &= unchecked((ushort)~(ushort)MethodAttributes.Static); break;
                case "ancestor-cctor-synchronized": method.Definition.iflags |= (ushort)MethodImplAttributes.Synchronized; break;
                case "ancestor-cctor-return": method.Definition.returnTypeIdx = _caller.AppContext.SystemTypes.SystemInt32Type.Definition!.ByvalTypeIndex; break;
                case "owner-ctor-static": method.Definition.flags |= (ushort)MethodAttributes.Static; break;
            }
            Assert.That(FindCaller(), Is.Null, mutation);
            if (mutation != "element") Assert.That(X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor), Is.Null, mutation);
        }
        finally
        {
            type.Definition.Bitfield = bitfield; type.Definition.Flags = typeFlags;
            method.Definition.flags = flags; method.Definition.iflags = implementation; method.Definition.returnTypeIdx = returnIndex;
        }
        AcceptBoth();
    }

    [TestCase("version")]
    [TestCase("public-key-token")]
    [TestCase("assembly-token")]
    [TestCase("image-token")]
    [TestCase("image-assembly")]
    [TestCase("module-name-pointer")]
    [TestCase("reference")]
    [TestCase("field-name")]
    [TestCase("parameter-name")]
    [TestCase("type-token")]
    [TestCase("method-token")]
    public void CanonicalOriginalIdentityIsRequiredBeforeAdmission(string mutation)
    {
        var assembly = _ancestor.DeclaringAssembly;
        var definition = assembly.Definition!;
        var image = definition.Image;
        var module = assembly.CodeGenModule!;
        var owner = _caller.DeclaringType!;
        var field = owner.Fields.Single(member => member.Name == "Value").BackingData!.Field;
        var parameter = owner.Methods.Single(method => method.Name == "Capture").Parameters[0].Definition!;
        var refIndex = owner.DeclaringAssembly.Definition!.ReferencedAssemblyStart;
        var version = definition.AssemblyName.major;
        var key = definition.AssemblyName.publicKeyToken;
        var token = definition.Token;
        var imageToken = image.token;
        var imageAssembly = image.assemblyIndex;
        var moduleName = module.moduleName;
        var reference = _caller.AppContext.Metadata.referencedAssemblies[refIndex];
        var fieldName = field.nameIndex;
        var parameterName = parameter.nameIndex;
        var typeToken = _ancestor.Definition!.Token;
        var methodToken = _caller.Definition!.token;
        try
        {
            switch (mutation)
            {
                case "version": definition.AssemblyName.major++; break;
                case "public-key-token": definition.AssemblyName.publicKeyToken ^= 1; break;
                case "assembly-token": definition.Token++; break;
                case "image-token": image.token++; break;
                case "image-assembly": image.assemblyIndex = owner.DeclaringAssembly.Definition!.Image.assemblyIndex; break;
                case "module-name-pointer": module.moduleName++; break;
                case "reference": _caller.AppContext.Metadata.referencedAssemblies[refIndex] = -1; break;
                case "field-name": field.nameIndex++; break;
                case "parameter-name": parameter.nameIndex++; break;
                case "type-token": _ancestor.Definition.Token++; break;
                case "method-token": _caller.Definition.token++; break;
            }
            Assert.That(FindCaller(), Is.Null, mutation);
            if (mutation != "method-token") Assert.That(X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor), Is.Null, mutation);
        }
        finally
        {
            definition.AssemblyName.major = version; definition.AssemblyName.publicKeyToken = key; definition.Token = token;
            image.token = imageToken; image.assemblyIndex = imageAssembly; module.moduleName = moduleName;
            _caller.AppContext.Metadata.referencedAssemblies[refIndex] = reference;
            field.nameIndex = fieldName; parameter.nameIndex = parameterName;
            _ancestor.Definition.Token = typeToken; _caller.Definition.token = methodToken;
        }
        AcceptBoth();
    }

    [TestCase("name")]
    [TestCase("token")]
    [TestCase("attributes")]
    [TestCase("getter")]
    [TestCase("setter")]
    public void RetainedAncestorPropertyRowsCannotChangeBehindCachedMembers(string mutation)
    {
        var property = _ancestor.Properties[0].Definition!;
        var name = property.nameIndex;
        var token = property.token;
        var attributes = property.attrs;
        var getter = property.get;
        var setter = property.set;
        try
        {
            switch (mutation)
            {
                case "name": property.nameIndex++; break;
                case "token": property.token++; break;
                case "attributes": property.attrs ^= 1; break;
                case "getter": property.get = Il2CppVariableWidthIndex<Il2CppMethodDefinition>.MakeTemporaryForFixedWidthUsage(int.MaxValue); break;
                case "setter": property.set = Il2CppVariableWidthIndex<Il2CppMethodDefinition>.MakeTemporaryForFixedWidthUsage(int.MaxValue); break;
            }
            Assert.That(FindCaller(), Is.Null, mutation);
            Assert.That(X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor), Is.Null, mutation);
        }
        finally
        {
            property.nameIndex = name;
            property.token = token;
            property.attrs = attributes;
            property.get = getter;
            property.set = setter;
        }
        AcceptBoth();
    }

    [TestCase("name")]
    [TestCase("token")]
    [TestCase("type")]
    [TestCase("adder")]
    [TestCase("remover")]
    [TestCase("invoker")]
    [TestCase("type-flags")]
    [TestCase("override-type")]
    public void OriginalEventRowsAndAccessorsRemainBounded(string mutation)
    {
        var type = _ancestor.AppContext.SystemTypes.SystemObjectType.DeclaringAssembly.Types.Single(candidate => candidate.FullName == "System.AppDomain");
        var member = type.Events.Single();
        var definition = member.Definition!;
        var name = definition.nameIndex;
        var token = definition.token;
        var typeIndex = definition.typeIndex;
        var adder = definition.add;
        var remover = definition.remove;
        var invoker = definition.raise;
        var raw = definition.RawType!;
        var bits = raw.Bits;
        var attributes = raw.Attrs;
        var overrideType = member.OverrideEventType;
        Assert.That(X64OriginalReferenceClassProof.IsValid(type, retainAncestorInitializers: true), Is.True);
        try
        {
            switch (mutation)
            {
                case "name": definition.nameIndex++; break;
                case "token": definition.token++; break;
                case "type": definition.typeIndex = Il2CppVariableWidthIndex<Il2CppType>.MakeTemporaryForFixedWidthUsage(int.MaxValue); break;
                case "adder": definition.add = Il2CppVariableWidthIndex<Il2CppMethodDefinition>.MakeTemporaryForFixedWidthUsage(int.MaxValue); break;
                case "remover": definition.remove = Il2CppVariableWidthIndex<Il2CppMethodDefinition>.MakeTemporaryForFixedWidthUsage(int.MaxValue); break;
                case "invoker": definition.raise = Il2CppVariableWidthIndex<Il2CppMethodDefinition>.MakeTemporaryForFixedWidthUsage(int.MaxValue); break;
                case "type-flags": raw.Attrs ^= 1; raw.Bits ^= 1; break;
                case "override-type": member.OverrideEventType = type; break;
            }
            Assert.That(X64OriginalReferenceClassProof.IsValid(type, retainAncestorInitializers: true), Is.False, mutation);
        }
        finally
        {
            definition.nameIndex = name;
            definition.token = token;
            definition.typeIndex = typeIndex;
            definition.add = adder;
            definition.remove = remover;
            definition.raise = invoker;
            raw.Bits = bits;
            raw.Attrs = attributes;
            member.OverrideEventType = overrideType;
        }
        Assert.That(X64OriginalReferenceClassProof.IsValid(type, retainAncestorInitializers: true), Is.True);
        AcceptBoth();
    }

    [TestCase("owner", "count")]
    [TestCase("owner", "table")]
    [TestCase("ancestor", "count")]
    [TestCase("ancestor", "table")]
    public void OriginalCodegenModuleRecordsCannotUseMutatedPointerCaches(string role, string mutation)
    {
        var module = (role == "owner" ? _caller.DeclaringType! : _ancestor).DeclaringAssembly.CodeGenModule!;
        var count = module.methodPointerCount;
        var table = module.methodPointers;
        try
        {
            if (mutation == "count") module.methodPointerCount++;
            else module.methodPointers += sizeof(ulong);
            Assert.That(FindCaller(), Is.Null);
            Assert.That(X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor), Is.Null);
        }
        finally { module.methodPointerCount = count; module.methodPointers = table; }
        AcceptBoth();
    }

    [TestCase("caller")]
    [TestCase("callee")]
    [TestCase("constructor")]
    [TestCase("base")]
    public void ConsumedOrdinaryMethodPointerSlotsRetainTheirNativeRegistration(string role)
    {
        var method = role switch
        {
            "callee" => _caller.DeclaringType!.Methods.Single(candidate => candidate.Name == "Capture"),
            "constructor" => _constructor,
            "base" => _ancestor.Methods.Single(candidate => candidate.Name == ".ctor"),
            _ => _caller
        };
        var app = method.AppContext;
        var module = method.DeclaringType!.DeclaringAssembly.CodeGenModule!;
        var pointers = app.Binary.GetCodegenModuleMethodPointers(app.Binary.GetCodegenModuleIndex(module));
        var index = checked((int)(method.Definition!.token & 0x00FFFFFF) - 1);
        var pointer = pointers[index];
        try
        {
            pointers[index] += sizeof(ulong);
            if (role is "caller" or "callee") Assert.That(FindCaller(), Is.Null);
            else Assert.That(X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor), Is.Null);
        }
        finally { pointers[index] = pointer; }
        AcceptBoth();
    }

    [TestCase("name")]
    [TestCase("token")]
    [TestCase("owner")]
    public void EveryOrdinaryFoldedBaseBindingRetainsItsOriginalDeclaration(string mutation)
    {
        var original = X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor)!;
        var alias = _caller.AppContext.MethodsByAddress[original.BaseConstructor.UnderlyingPointer]
            .First(method => method.Definition != null && !ReferenceEquals(method, original.BaseConstructor));
        var definition = alias.Definition!;
        var name = definition.nameIndex;
        var token = definition.token;
        var owner = definition.declaringTypeIdx;
        try
        {
            switch (mutation)
            {
                case "name": definition.nameIndex++; break;
                case "token": definition.token++; break;
                case "owner": definition.declaringTypeIdx = Il2CppVariableWidthIndex<Il2CppTypeDefinition>.MakeTemporaryForFixedWidthUsage(int.MaxValue); break;
            }
            Assert.That(X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor), Is.Null, mutation);
        }
        finally { definition.nameIndex = name; definition.token = token; definition.declaringTypeIdx = owner; }
        AcceptBoth();
    }

    [TestCase("pointer")]
    [TestCase("adjustor")]
    [TestCase("table")]
    [TestCase("specification")]
    [TestCase("arguments")]
    [TestCase("argument-width")]
    public void ConcreteFoldedRegistrationsRemainOriginalBeforeLazyInstantiation(string mutation)
    {
        var original = X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor)!;
        var app = _caller.AppContext;
        var concrete = app.MethodsByAddress[original.BaseConstructor.UnderlyingPointer]
            .OfType<ConcreteGenericMethodAnalysisContext>().First(method => method.TypeGenericParameters.Count != 0);
        var reference = concrete.MethodRef!;
        Assert.That(app.Binary.TryGetGenericMethodRegistration(reference, out var origin), Is.True);
        var table = app.Metadata.genericMethodTables[origin.TableIndex];
        var specification = app.Metadata.AllGenericMethodSpecs[origin.SpecificationIndex];
        var instance = app.Binary.GetGenericInst(specification.classIndexIndex);
        var pointer = reference.GenericVariantPtr;
        var adjustor = reference.AdjustorThunkPtr;
        var tableMethod = table.methodIndex;
        var classIndex = specification.classIndexIndex;
        var start = instance.pointerStart;
        var count = instance.pointerCount;
        try
        {
            switch (mutation)
            {
                case "pointer": reference.GenericVariantPtr += sizeof(ulong); break;
                case "adjustor": reference.AdjustorThunkPtr = 1; break;
                case "table": table.methodIndex++; break;
                case "specification": specification.classIndexIndex = Il2CppVariableWidthIndex<Il2CppGenericInst>.MakeTemporaryForFixedWidthUsage(int.MaxValue); break;
                case "arguments": instance.pointerStart += sizeof(ulong); break;
                case "argument-width": instance.pointerCount += 1UL << 32; break;
            }
            Assert.That(X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor), Is.Null, mutation);
        }
        finally
        {
            reference.GenericVariantPtr = pointer;
            reference.AdjustorThunkPtr = adjustor;
            table.methodIndex = tableMethod;
            specification.classIndexIndex = classIndex;
            instance.pointerStart = start;
            instance.pointerCount = count;
        }
        AcceptBoth();
    }

    [Test]
    public void ANewConcreteReferenceCannotImpersonateAnOriginallyRegisteredAlias()
    {
        var original = X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor)!;
        var app = _caller.AppContext;
        var aliases = app.MethodsByAddress[original.BaseConstructor.UnderlyingPointer];
        var retained = aliases.OfType<ConcreteGenericMethodAnalysisContext>().First();
        Assert.That(app.Binary.TryGetGenericMethodRegistration(retained.MethodRef!, out var origin), Is.True);
        var reference = new Cpp2IlMethodRef(app.Metadata.AllGenericMethodSpecs[origin.SpecificationIndex])
        {
            GenericVariantPtr = retained.UnderlyingPointer,
            AdjustorThunkPtr = retained.MethodRef!.AdjustorThunkPtr
        };
        var concrete = new ConcreteGenericMethodAnalysisContext(reference, app);
        var references = app.Binary.ConcreteGenericMethods[reference.BaseMethod];
        references.Add(reference);
        app.ConcreteGenericMethodsByRef.Add(reference, concrete);
        aliases.Add(concrete);
        try { Assert.That(X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor), Is.Null); }
        finally
        {
            aliases.Remove(concrete);
            app.ConcreteGenericMethodsByRef.Remove(reference);
            references.Remove(reference);
        }
        AcceptBoth();
    }

    public static IEnumerable<TestCaseData> RawChanges()
    {
        foreach (var role in new[] { "owner", "ancestor", "array", "element", "return" })
            foreach (var change in new[] { "null-data", "union", "bits", "value-type", "wide-index" })
                yield return new TestCaseData(role, change);
    }

    [TestCaseSource(nameof(RawChanges))]
    public void MalformedRawDescriptorsCannotResolveThroughCachedContexts(string role, string change)
    {
        var array = _caller.DeclaringType!.Fields.Single(field => field.Name == "Items").BackingData!.Field.RawFieldType!;
        var raw = role switch
        {
            "owner" => _caller.DeclaringType.Definition!.RawType,
            "ancestor" => _caller.DeclaringType.Definition!.RawBaseType!,
            "element" => array.GetEncapsulatedType(),
            "return" => _caller.Definition!.RawReturnType!,
            _ => array
        };
        var data = raw.Data;
        var dummy = data.Dummy;
        var point = raw.Datapoint;
        var bits = raw.Bits;
        var valueType = raw.ValueType;
        try
        {
            switch (change)
            {
                case "null-data": raw.Data = null!; break;
                case "union": raw.Data.Dummy++; break;
                case "bits": raw.Bits ^= 1U << 16; break;
                case "value-type": raw.ValueType ^= 1; raw.Bits ^= 1U << 31; break;
                case "wide-index": raw.Data.Dummy += 1UL << 32; raw.Datapoint = raw.Data.Dummy; break;
            }
            Assert.That(FindCaller(), Is.Null, role + ": " + change);
        }
        finally { raw.Data = data; data.Dummy = dummy; raw.Datapoint = point; raw.Bits = bits; raw.ValueType = valueType; }
        AcceptBoth();
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(6)]
    [TestCase(11)]
    [TestCase(16)]
    public void EveryConstructorNativeRegionRemainsFileBacked(int instruction)
    {
        var cache = _constructor.RawBytes;
        var decoded = Cpp2IL.Core.Utils.X86Utils.Iterate(_constructor).ToArray();
        var bytes = cache.AsSpan().ToArray();
        bytes[checked((int)(decoded[instruction].IP - _constructor.UnderlyingPointer))] ^= 1;
        try
        {
            _constructor.RawBytes = new BinarySlice(bytes);
            Assert.That(X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor), Is.Null);
        }
        finally { _constructor.RawBytes = cache; }
        AcceptBoth();
    }

    [Test]
    public void FoldedBaseTargetDoesNotAcquireArtificialUniqueness()
    {
        var original = X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor)!;
        var aliases = _caller.AppContext.MethodsByAddress[original.BaseConstructor.UnderlyingPointer];
        Assert.That(aliases.Count, Is.GreaterThan(1));
        aliases.Add(original.BaseConstructor);
        try { Assert.That(X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor), Is.Null); }
        finally { aliases.RemoveAt(aliases.Count - 1); }
        AcceptBoth();
    }

    [Test]
    public void FinalCallerGraphKeepsTheZeroIndexAndCapturedReferenceArgument()
    {
        _caller.Analyze();
        IlGenerator.ValidateGuardedArrayOperations(_caller);
        var access = _caller.ControlFlowGraph!.Instructions.SelectMany(instruction => instruction.Operands).OfType<ArrayAccess>().Single();
        var index = access.Index;
        try
        {
            access.Index = new Immediate(1);
            Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(_caller));
        }
        finally { access.Index = index; }
        IlGenerator.ValidateGuardedArrayOperations(_caller);
    }

    private X64GuardedArrayOperationProof.Evidence? FindCaller() => X64GuardedArrayOperationProof.Find(_caller, _body);

    private void AcceptBoth()
    {
        Assert.That(FindCaller(), Is.Not.Null);
        Assert.That(X64GuardedBaseConstructorProof.FindRetainedAncestor(_constructor), Is.Not.Null);
    }
}
