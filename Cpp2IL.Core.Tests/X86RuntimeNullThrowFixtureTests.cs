using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using IsilOpCode = Cpp2IL.Core.ISIL.OpCode;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact native-input proof check; this is not a loop behavior acceptance gate.</summary>
[NonParallelizable]
public class X86RuntimeNullThrowFixtureTests
{
    [Test]
    public void ConstructorBindingRequiresUnchangedUnambiguousCorlibMetadata()
    {
        Cpp2IlApi.ResetInternalState();
        try
        {
            var app = TestGameLoader.LoadSimple2019Game();
            var constructor = X86RuntimeNullThrowProof.BindIdentity(app);
            Assert.That(constructor, Is.Not.Null);
            var type = constructor!.DeclaringType!;
            var corlib = type.DeclaringAssembly;
            var originalAttributes = constructor.Attributes;
            var originalImpl = constructor.ImplAttributes;
            var originalBase = type.BaseType;
            var originalVersion = corlib.Version;
            var originalToken = corlib.PublicKeyToken;
            var rawReturn = constructor.Definition!.RawReturnType!;
            var originalDeclaringIndex = type.Definition!.DeclaringTypeIndex;
            void Reject(Action mutate, Action restore)
            {
                mutate();
                Assert.That(X86RuntimeNullThrowProof.BindIdentity(app), Is.Null);
                restore();
                Assert.That(X86RuntimeNullThrowProof.BindIdentity(app), Is.SameAs(constructor));
            }
            Reject(() => constructor.Name = ".cctor", () => constructor.Name = ".ctor");
            Reject(() => constructor.Attributes |= MethodAttributes.Static, () => constructor.Attributes = originalAttributes);
            Reject(() => constructor.ImplAttributes |= MethodImplAttributes.Native, () => constructor.ImplAttributes = originalImpl);
            Reject(() => constructor.ReturnType = app.SystemTypes.SystemInt32Type, () => constructor.ReturnType = app.SystemTypes.SystemVoidType);
            Reject(() => type.Methods.Add(constructor), () => type.Methods.RemoveAt(type.Methods.Count - 1));
            Reject(() => type.Namespace = "Other", () => type.Namespace = "System");
            Reject(() => type.Name = "OtherException", () => type.Name = "NullReferenceException");
            Reject(() => type.DeclaringType = app.SystemTypes.SystemObjectType, () => type.DeclaringType = null);
            Reject(() => type.Definition.DeclaringTypeIndex = app.SystemTypes.SystemObjectType.Definition!.ByvalTypeIndex,
                () => type.Definition.DeclaringTypeIndex = originalDeclaringIndex);
            Reject(() => type.BaseType = app.SystemTypes.SystemObjectType, () => type.BaseType = originalBase);
            Reject(() => rawReturn.NumMods = 1, () => rawReturn.NumMods = 0);
            Reject(() => rawReturn.Byref = 1, () => rawReturn.Byref = 0);
            Reject(() => rawReturn.Pinned = 1, () => rawReturn.Pinned = 0);
            Reject(() => corlib.Name = "Other", () => corlib.Name = "mscorlib");
            Reject(() => corlib.Version = new Version(99, 0), () => corlib.Version = originalVersion);
            Reject(() => corlib.PublicKeyToken = [1, 2, 3], () => corlib.PublicKeyToken = originalToken);
            Assert.That(X86RuntimeNullThrowProof.TryIdentify(app, 0x1000), Is.Null,
                "The independently tested binder does not qualify an unsupported Unity profile.");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void ExactLoopNativeInputsIdentifyRuntimeNullThrowAndKeepEvidenceBoundToCurrentIdentity()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_LOOP_CALL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_LOOP_CALL_FIXTURE_INPUT to the public LoopCallFixture's exact player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var fixture = app.GetAssemblyByName("LoopCallFixture");
            Assert.That(fixture, Is.Not.Null, "No fallback input is used; build the separate public source fixture first.");
            var methods = fixture!.Types.SelectMany(t => t.Methods).ToArray();
            Assert.That(methods.Select(m => m.Name), Is.EquivalentTo(new[] { ".ctor", "Step", "Run", "RunUntil" }));
            Assert.That(fixture.Types.Where(t => t.Name != "<Module>").Select(t => t.FullName),
                Is.EqualTo(new[] { "LoopCallFixture.LoopState" }));
            var evidence = methods.SelectMany(method =>
            {
                var matches = X86Utils.Iterate(method).Where(i => i.Code == Code.Call_rel32_64)
                    .Select(i => X86RuntimeNullThrowProof.TryIdentify(app, i.NearBranchTarget))
                    .Where(match => match != null).Cast<RuntimeNullThrowEvidence>().ToArray();
                Assert.That(matches, Has.Length.EqualTo(method.Name is "Run" or "RunUntil" ? 1 : 0));
                Assert.That(matches.All(match => match.IsValidFor(app)), Is.True);
                return matches;
            }).ToArray();
            Assert.That(evidence.Select(match => match.NativeTarget).Distinct().ToArray(), Has.Length.EqualTo(1));
            var identity = X86RuntimeNullThrowProof.BindIdentity(app)!;
            var type = identity.DeclaringType!;
            Assert.That(type.FullName, Is.EqualTo("System.NullReferenceException"));
            Assert.That(identity.Parameters, Is.Empty);
            Assert.That(identity.Name, Is.EqualTo(".ctor"));
            void Reject(Action mutate, Action restore)
            {
                mutate();
                Assert.That(evidence.All(match => !match.IsValidFor(app)), Is.True);
                Assert.That(X86RuntimeNullThrowProof.TryIdentify(app, evidence[0].NativeTarget), Is.Null);
                restore();
                Assert.That(evidence.All(match => match.IsValidFor(app)), Is.True);
            }
            var attributes = identity.Attributes;
            Reject(() => identity.Attributes |= MethodAttributes.Static, () => identity.Attributes = attributes);
            Reject(() => identity.ReturnType = app.SystemTypes.SystemInt32Type,
                () => identity.ReturnType = app.SystemTypes.SystemVoidType);
            Reject(() => type.Methods.Add(identity), () => type.Methods.RemoveAt(type.Methods.Count - 1));
            Reject(() => type.Namespace = "Other", () => type.Namespace = "System");
            Reject(() => type.DeclaringType = app.SystemTypes.SystemObjectType, () => type.DeclaringType = null);
            var architecture = app.Binary.InstructionSetId;
            Reject(() => app.Binary.InstructionSetId = DefaultInstructionSets.ARM_V8,
                () => app.Binary.InstructionSetId = architecture);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
