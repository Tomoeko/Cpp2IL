using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using LibCpp2IL.PE;
using ManagedFieldAttributes = AsmResolver.PE.DotNet.Metadata.Tables.FieldAttributes;
using ManagedMethodAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64InheritedInt32ConstructorProofTests
{
    [Test]
    public void ExactSharedRootWritesInheritedPublicInt32AfterImmediateBase()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_CONSTRUCTOR_THUNK_CHAIN_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CONSTRUCTOR_THUNK_CHAIN_FIXTURE_INPUT to the neutral exact player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName("ConstructorThunkChainFixture")!;
            var roots = new[] { "FirstRoot", "SecondRoot" }
                .Select(name => assembly.Types.Single(type => type.Name == name)
                    .Methods.Single(method => method.Name == ".ctor"))
                .ToArray();
            var payloadConstructor = assembly.Types.Single(type =>
                type.Name == "PayloadBase").Methods.Single(method =>
                method.Name == ".ctor");
            payloadConstructor.EnsureRawBytes();
            Assert.That(X64ObjectConstructorThunkProof.Find(payloadConstructor,
                X86Utils.Iterate(payloadConstructor).ToArray()), Is.Not.Null);
            foreach (var root in roots)
            {
                root.EnsureRawBytes();
                var rootNative = X86Utils.Iterate(root).ToArray();
                var unwind = X64UnwindProof.ForApplication(app)!;
                var span = unwind.ClassifySpan(root.UnderlyingPointer,
                    root.UnderlyingPointer + 29);
                Assert.That(X64AncestorConstructorThunkProof.OrdinaryConstructor(root),
                    Is.True, "root metadata");
                Assert.That(X64AncestorConstructorThunkProof.OrdinaryOwner(
                    root.DeclaringType), Is.True, "root type metadata");
                Assert.That(root.RawBytes.Length, Is.EqualTo(29));
                Assert.That(span.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
                Assert.That(span.End - span.Start, Is.EqualTo(29));
                Assert.That(unwind.MatchesUnwind(root.UnderlyingPointer,
                    root.UnderlyingPointer + 29, 6, 0,
                    new byte[] { 0x06, 0x32, 0x02, 0x30 }), Is.True,
                    "root unwind encoding");
                Assert.That(X64InheritedInt32ConstructorProof.TryProveBody(
                    rootNative, root.UnderlyingPointer,
                    rootNative[4].NearBranchTarget,
                    rootNative[5].MemoryDisplacement64, out _), Is.True,
                    "complete root native shape");
                var rootEvidence = X64InheritedInt32ConstructorProof.Find(root);
                Assert.That(rootEvidence, Is.Not.Null);
                Assert.That(rootEvidence!.BaseConstructor,
                    Is.SameAs(payloadConstructor));
                Assert.That(rootEvidence.Field.Name, Is.EqualTo("State"));
                Assert.That(rootEvidence.Field.Offset, Is.EqualTo(16));
                Assert.That(rootEvidence.Value, Is.EqualTo(29));
            }
            var rootMethod = roots[0];
            var rootBody = X86Utils.Iterate(rootMethod).ToArray();
            var wrongRootCall = rootBody.ToArray();
            wrongRootCall[4].NearBranch64++;
            Assert.That(X64InheritedInt32ConstructorProof.Find(rootMethod,
                wrongRootCall), Is.Null);
            var wrongRootStore = rootBody.ToArray();
            wrongRootStore[5].MemoryDisplacement64 += 8;
            Assert.That(X64InheritedInt32ConstructorProof.Find(rootMethod,
                wrongRootStore), Is.Null);
            var wrongRootValue = rootBody.ToArray();
            wrongRootValue[5].Immediate32++;
            Assert.That(X64InheritedInt32ConstructorProof.Find(rootMethod,
                wrongRootValue), Is.Null);
            var rootAliases = app.MethodsByAddress[rootMethod.UnderlyingPointer];
            var unrelated = app.SystemTypes.SystemObjectType.Methods.First(method =>
                method.Name == "ToString" && method.Parameters.Count == 0);
            try
            {
                rootAliases.Add(unrelated);
                Assert.That(X64InheritedInt32ConstructorProof.Find(rootMethod,
                    rootBody), Is.Null, "Mixed constructor/method aliases are unsafe.");
            }
            finally { rootAliases.Remove(unrelated); }
            var stateField = payloadConstructor.DeclaringType!.Fields.Single(field =>
                field.Name == "State");
            try
            {
                stateField.OverrideFieldType = app.SystemTypes.SystemObjectType;
                Assert.That(X64InheritedInt32ConstructorProof.Find(rootMethod,
                    rootBody), Is.Null, "The inherited Int32 layout must remain exact.");
            }
            finally { stateField.OverrideFieldType = null; }
            try
            {
                stateField.Attributes = FieldAttributes.Private;
                Assert.That(X64InheritedInt32ConstructorProof.Find(rootMethod,
                    rootBody), Is.Null, "The inherited write must be source-accessible.");
            }
            finally { stateField.Attributes = stateField.DefaultAttributes; }
            try
            {
                rootMethod.DeclaringType!.BaseType = app.SystemTypes.SystemObjectType;
                Assert.That(X64InheritedInt32ConstructorProof.Find(rootMethod,
                    rootBody), Is.Null, "The immediate base must remain unchanged.");
            }
            finally { rootMethod.DeclaringType!.OverrideBaseType = null; }
            Assert.That(X64InheritedInt32ConstructorProof.Find(rootMethod, rootBody),
                Is.Not.Null);

            var rootModule = new ModuleDefinition("ConstructorRootProof.dll",
                new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
            var rootSignature = MethodSignature.CreateInstance(
                rootModule.CorLibTypeFactory.Void);
            var payloadDefinition = new MethodDefinition(".ctor",
                ManagedMethodAttributes.Public | ManagedMethodAttributes.SpecialName |
                ManagedMethodAttributes.RuntimeSpecialName, rootSignature);
            var stateDefinition = new FieldDefinition("State",
                ManagedFieldAttributes.Public, rootModule.CorLibTypeFactory.Int32);
            var rootDefinition = new MethodDefinition(".ctor",
                ManagedMethodAttributes.Public | ManagedMethodAttributes.SpecialName |
                ManagedMethodAttributes.RuntimeSpecialName, rootSignature);
            payloadConstructor.PutExtraData("AsmResolverMethod", payloadDefinition);
            stateField.PutExtraData("AsmResolverField", stateDefinition);
            Assert.That(X64InheritedInt32ConstructorRecovery.TryGenerate(rootMethod,
                rootDefinition), Is.True);
            Assert.That(rootDefinition.CilMethodBody!.Instructions.Select(instruction =>
                instruction.OpCode), Is.EqualTo(new[] { CilOpCodes.Ldarg_0,
                CilOpCodes.Call, CilOpCodes.Ldarg_0, CilOpCodes.Ldc_I4,
                CilOpCodes.Stfld, CilOpCodes.Ret }));
            Assert.That(rootDefinition.CilMethodBody.Instructions[1].Operand,
                Is.SameAs(payloadDefinition));
            Assert.That(rootDefinition.CilMethodBody.Instructions[3].Operand,
                Is.EqualTo(29));
            Assert.That(rootDefinition.CilMethodBody.Instructions[4].Operand,
                Is.SameAs(stateDefinition));
            var pe = (PE)app.Binary;
            var offset = checked((int)pe.MapVirtualAddressToRaw(
                rootMethod.UnderlyingPointer, false));
            var modifiedBinary = File.ReadAllBytes(binary);
            Assert.That(modifiedBinary[offset], Is.EqualTo((byte)0x40));
            modifiedBinary[offset] = 0x90;
            var metadataBytes = File.ReadAllBytes(metadata);
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(modifiedBinary, metadataBytes,
                UnityVersion.Parse("2021.3.35f1"));
            var changed = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ConstructorThunkChainFixture")!.Types
                .Single(type => type.Name == "FirstRoot").Methods
                .Single(method => method.Name == ".ctor");
            Assert.That(X64InheritedInt32ConstructorProof.Find(changed), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
