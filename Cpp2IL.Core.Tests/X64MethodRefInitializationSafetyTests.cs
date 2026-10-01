using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Reporting;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64MethodRefInitializationSafetyTests
{
    [TestCase("CPP2IL_GENERIC_DISPATCH_FIXTURE_INPUT", "GenericDispatchFixture", "Forwarder", 2)]
    [TestCase("CPP2IL_BOOLEAN_GETTER_METADATA_FIXTURE_INPUT", "BooleanGetterMetadataFixture", "GenericFieldOwner", 1)]
    public void UnqualifiedMethodRefCannotFallThroughToEmittedOutput(string environment,
        string assemblyName, string ownerName, int expectedMethods)
    {
        var input = Environment.GetEnvironmentVariable(environment);
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set " + environment + " to the neutral player-input directory.");
        var original = File.ReadAllBytes(Path.Combine(input!, "GameAssembly.dll"));
        var metadata = File.ReadAllBytes(Directory.EnumerateFiles(input!,
            "global-metadata.dat", SearchOption.AllDirectories).Single());
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Initialize(original, metadata);
            AssertRejected(assemblyName, ownerName, expectedMethods, methodDefExpected: true);
            var app = Cpp2IlApi.CurrentAppContext!;
            var pe = (PE)app.Binary;
            var edges = ResolverEdges(app, pe);
            var slots = app.GetAssemblyByName(assemblyName)!.Types.Single(type => type.Name == ownerName)
                .Methods.Where(method => expectedMethods == 1 ? method.Name == ".ctor" :
                    method.Name.Contains("Read", StringComparison.Ordinal))
                .Select(method => X64MetadataInitializationHelperProof.CheckGuardedMethodRef(method).Slot).ToArray();
            var invalidIndex = checked((uint)app.LibCpp2IlContext.Metadata.AllGenericMethodSpecs.Length + 1);
            foreach (var (edge, methodDefExpected) in new[]
                     { (edges[0], true), (edges[1], true), (edges[2], false) })
            {
                var changed = (byte[])original.Clone();
                var raw = checked((int)pe.MapVirtualAddressToRaw(edge.IP, false));
                Assert.That(edge.Length, Is.EqualTo(5));
                changed[raw + 1] ^= 1;
                Initialize(changed, metadata);
                AssertRejected(assemblyName, ownerName, expectedMethods, methodDefExpected);
                Initialize(original, metadata);
                AssertRejected(assemblyName, ownerName, expectedMethods, methodDefExpected: true);
            }
            foreach (var slot in slots)
            {
                var changed = (byte[])original.Clone();
                var raw = checked((int)pe.MapVirtualAddressToRaw(slot, false));
                var encoded = ((ulong)MetadataUsageType.MethodRef << 29) | ((ulong)invalidIndex << 1) | 1;
                BinaryPrimitives.WriteUInt64LittleEndian(changed.AsSpan(raw, 8), encoded);
                Initialize(changed, metadata);
                var malformedApp = Cpp2IlApi.CurrentAppContext!;
                Assert.That(malformedApp.LibCpp2IlContext.GetMethodGlobalByAddress(slot), Is.Null);
                AssertRejected(assemblyName, ownerName, expectedMethods, methodDefExpected: true,
                    invalidSlot: slot);
                Initialize(original, metadata);
                AssertRejected(assemblyName, ownerName, expectedMethods, methodDefExpected: true);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void Initialize(byte[] binary, byte[] metadata)
    {
        Cpp2IlApi.ResetInternalState();
        Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
    }

    private static void AssertRejected(string assemblyName, string ownerName,
        int expectedMethods, bool methodDefExpected, ulong invalidSlot = 0)
    {
        var app = Cpp2IlApi.CurrentAppContext!;
        var owner = app.GetAssemblyByName(assemblyName)!.Types.Single(type => type.Name == ownerName);
        var methods = owner.Methods.Where(method => expectedMethods == 1
            ? method.Name == ".ctor" : method.Name.Contains("Read", StringComparison.Ordinal)).ToArray();
        Assert.That(methods, Has.Length.EqualTo(expectedMethods));
        var pe = (PE)app.Binary;
        var unwind = X64UnwindProof.ForApplication(app)!;
        var initializer = app.GetOrCreateKeyFunctionAddresses().il2cpp_codegen_initialize_runtime_metadata;
        // An unrelated tag-6 mutation must not revoke a genuine tag-3 proof.
        Assert.That(X64MetadataInitializationHelperProof.TryIdentifyMethodDefArm(app,
            pe, unwind, initializer), Is.EqualTo(methodDefExpected));
        foreach (var method in methods)
        {
            var obligation = X64MetadataInitializationHelperProof.CheckGuardedMethodRef(method);
            Assert.That(obligation.Disposition,
                Is.EqualTo(X64MetadataInitializationHelperProof.MethodRefDisposition.Unsupported));
            Assert.That(obligation.Usage!.Type, Is.EqualTo(MetadataUsageType.MethodRef));
            Assert.That(obligation.Usage.IsValid, Is.EqualTo(obligation.Slot != invalidSlot));
            Assert.That(X64MetadataInitializationHelperProof.TryIdentifyMethodRefArm(app,
                pe, unwind, obligation.Initializer, obligation.Usage, obligation.Slot,
                out var reason), Is.False);
            Assert.That(reason, Is.EqualTo(X64MetadataInitializationHelperProof.UnsupportedMethodRefReason));
            Assert.That(expectedMethods == 1 ? X64GenericBaseConstructorProof.Find(method) :
                (object?)X64GenericInterfaceWrapperProof.Find(method), Is.Null);

            var module = new ModuleDefinition("RecoverySafety.dll",
                new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
            var type = new TypeDefinition("Fixture", "Probe",
                AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public);
            module.TopLevelTypes.Add(type);
            var definition = new MethodDefinition(method.Name,
                AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes.Public,
                MethodSignature.CreateInstance(method.IsVoid
                    ? module.CorLibTypeFactory.Void : module.CorLibTypeFactory.Object));
            type.Methods.Add(definition);
            var output = new InspectableRecoveryOutput();
            output.Begin(app);
            output.Fill(definition, method);
            var report = output.Snapshot(app);
            var selected = report.Methods.Single(result => result.AssemblyName == assemblyName &&
                result.Signature == method.FullNameWithSignature);
            Assert.That(selected.Disposition, Is.EqualTo(MethodRecoveryDisposition.Failed));
            Assert.That(selected.ManagedIlValidation, Is.EqualTo("NotRun"));
            Assert.That(selected.Reasons.Any(value => value.Contains(reason, StringComparison.Ordinal)), Is.True);
            Assert.That(definition.CilMethodBody!.Instructions.Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { CilOpCodes.Ldstr, CilOpCodes.Newobj, CilOpCodes.Throw }));
            Assert.Throws<IncompleteRecoveryException>(() => report.EnsureComplete(new[] { assemblyName }));
        }
    }

    private static Instruction[] ResolverEdges(ApplicationAnalysisContext app, PE pe)
    {
        var unwind = X64UnwindProof.ForApplication(app)!;
        var thunk = Decode(app.GetOrCreateKeyFunctionAddresses().il2cpp_codegen_initialize_runtime_metadata, 1);
        var wrapper = Decode(thunk[0].NearBranchTarget, 2);
        var core = wrapper[1].NearBranchTarget;
        var span = unwind.ClassifySpan(core, core + 1);
        var alternate = span.End - core == 0x37;
        var dispatch = Decode(span.End, alternate ? 15 : 8);
        var table = dispatch[alternate ? 12 : 1];
        var tableRaw = checked((int)pe.MapVirtualAddressToRaw(unwind.ImageBase +
            checked((uint)table.MemoryDisplacement64), false));
        var definitionArm = BinaryPrimitives.ReadInt32LittleEndian(pe.GetRawBinaryContent().Slice(tableRaw + 8, 4));
        var referenceArm = BinaryPrimitives.ReadInt32LittleEndian(pe.GetRawBinaryContent().Slice(tableRaw + 20, 4));
        Assert.That(referenceArm, Is.EqualTo(definitionArm));
        Assert.That(definitionArm, Is.GreaterThanOrEqualTo(0));
        var arm = Decode(unwind.ImageBase + (uint)definitionArm, alternate ? 3 : 4);
        var decoder = Decode(arm[alternate ? 0 : 1].NearBranchTarget, 29);
        Assert.That(decoder[9].Mnemonic, Is.EqualTo(Mnemonic.Cmp));
        Assert.That(decoder[9].Op0Register, Is.EqualTo(Register.ECX));
        Assert.That(decoder[9].Immediate8, Is.EqualTo(6));
        Assert.That(decoder[12].Code, Is.EqualTo(Code.Call_rel32_64));
        Assert.That(decoder[15].Code, Is.EqualTo(Code.Jmp_rel32_64));
        Assert.That(decoder[18].Code, Is.EqualTo(Code.Jmp_rel32_64));
        return new[] { decoder[12], decoder[15], decoder[18] };

        Instruction[] Decode(ulong address, int count)
        {
            var raw = checked((int)pe.MapVirtualAddressToRaw(address, false));
            var bytes = pe.GetRawBinaryContent().Slice(raw,
                Math.Min(1024, pe.GetRawBinaryContent().Length - raw)).ToArray();
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), address);
            var body = Enumerable.Range(0, count).Select(_ => decoder.Decode()).ToArray();
            Assert.That(body.All(instruction => !instruction.IsInvalid), Is.True);
            return body;
        }
    }

    private sealed class InspectableRecoveryOutput : AsmResolverDllOutputFormatIlRecovery
    {
        internal void Begin(ApplicationAnalysisContext app) => BeginRecoveryReport(app);
        internal void Fill(MethodDefinition definition, MethodAnalysisContext method) => FillMethodBody(definition, method);
        internal RecoveryReport Snapshot(ApplicationAnalysisContext app) => CreateRecoveryReport(app, true);
    }
}
