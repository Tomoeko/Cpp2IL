using System;
using System.IO;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests.Isil;

public class X64InstanceByrefThrowFixtureTests
{
    [TestCase("ThrowSingle", 1)]
    [TestCase("ThrowThree", 3)]
    [NonParallelizable]
    public void OriginalByrefThrowKeepsHelperAbiGapAndRejectsChangedInputs(string name, int parameterCount)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_NATIVE_INSTANCE_BYREF_THROW_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NATIVE_INSTANCE_BYREF_THROW_FIXTURE_INPUT to the neutral exact-target player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("NativeInstanceByrefThrowFixture")!.Types.Single(type => type.Name == "ThrowCases");
            var method = owner.Methods.Single(item => item.Name == name);
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(method.Parameters.Count, Is.EqualTo(parameterCount));
            Assert.That(X64TerminalManagedThrowProof.TryProveCallerShape(native.Take(17).ToArray()), Is.True);
            Assert.That(X64TerminalManagedThrowProof.Find(method, native), Is.Null,
                "An instance byref signature remains outside the complete helper ABI proof.");
            var proof = X64TerminalManagedThrowProof.FindPartialInstanceByref(method, native);
            Assert.That(proof, Is.Not.Null);
            Assert.That(proof!.ExceptionType.Name, Is.EqualTo("NotSupportedException"));

            var changed = native.ToArray();
            changed[2].Code = Iced.Intel.Code.Mov_r64_rm64;
            Assert.That(X64TerminalManagedThrowProof.FindPartialInstanceByref(method, changed), Is.Null);
            var parameter = method.Parameters[0];
            try
            {
                parameter.ParameterType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64TerminalManagedThrowProof.FindPartialInstanceByref(method, native), Is.Null);
            }
            finally { parameter.OverrideParameterType = null; }
            try
            {
                parameter.Attributes |= System.Reflection.ParameterAttributes.Out;
                Assert.That(X64TerminalManagedThrowProof.FindPartialInstanceByref(method, native), Is.Null);
            }
            finally { parameter.OverrideAttributes = null; }
            try
            {
                method.OverrideReturnType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64TerminalManagedThrowProof.FindPartialInstanceByref(method, native), Is.Null,
                    "An output type override cannot replace the original return descriptor.");
            }
            finally { method.OverrideReturnType = null; }
            var originalToken = method.Definition!.token;
            try
            {
                method.Definition.token++;
                Assert.That(X64TerminalManagedThrowProof.FindPartialInstanceByref(method, native), Is.Null,
                    "Mutable cached metadata cannot replace the original MethodDef identity.");
            }
            finally { method.Definition.token = originalToken; }
            var originalReturn = method.Definition.returnTypeIdx;
            try
            {
                method.Definition.returnTypeIdx = LibCpp2IL.Metadata.Il2CppVariableWidthIndex<LibCpp2IL.BinaryStructures.Il2CppType>
                    .MakeTemporaryForFixedWidthUsage(int.MaxValue);
                Assert.That(X64TerminalManagedThrowProof.FindPartialInstanceByref(method, native), Is.Null,
                    "An invalid cached type index must be rejected before lazy type lookup.");
            }
            finally { method.Definition.returnTypeIdx = originalReturn; }
            var aliases = app.MethodsByAddress[method.UnderlyingPointer];
            aliases.Add(proof.Constructor);
            try { Assert.That(X64TerminalManagedThrowProof.FindPartialInstanceByref(method, native), Is.Null); }
            finally { aliases.Remove(proof.Constructor); }

            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            Assert.That(X64InstanceByrefThrowRecovery.TryGeneratePartial(method, output, out var reasons), Is.True);
            Assert.That(reasons, Has.Length.EqualTo(1));
            Assert.That(reasons[0], Does.StartWith("THROW-HELPER-ABI:"));
            Assert.That(output.CilMethodBody!.Instructions.Select(item => item.OpCode),
                Is.EqualTo(new[] { CilOpCodes.Newobj, CilOpCodes.Throw }));
            output.CilMethodBody.VerifyLabels();
            Assert.That(output.CilMethodBody.ComputeMaxStack(), Is.EqualTo(1));
            var previous = output.CilMethodBody;
            var constructor = proof.Constructor.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            var originalName = constructor.Name;
            try
            {
                constructor.Name = "ChangedConstructor";
                Assert.That(X64InstanceByrefThrowRecovery.TryGeneratePartial(method, output, out _), Is.False);
                Assert.That(output.CilMethodBody, Is.SameAs(previous));
            }
            finally { constructor.Name = originalName; }
            output.Signature!.HasThis = false;
            try
            {
                Assert.That(X64InstanceByrefThrowRecovery.TryGeneratePartial(method, output, out _), Is.False);
                Assert.That(output.CilMethodBody, Is.SameAs(previous));
            }
            finally { output.Signature.HasThis = true; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
