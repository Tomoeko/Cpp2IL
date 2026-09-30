using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64CallResultBooleanParameterFixtureTests
{
    [Test]
    public void ForwardedBooleanRetainsItsNativeOriginAndBoundCalls()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_CALL_RESULT_BOOLEAN_TAIL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CALL_RESULT_BOOLEAN_TAIL_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var callers = app.GetAssemblyByName("CallResultBooleanTailFixture")!.Types
                .SelectMany(type => type.Methods).Where(method => method.Name.StartsWith("Forward", StringComparison.Ordinal)).ToArray();
            Assert.That(callers, Has.Length.EqualTo(6));
            foreach (var caller in callers)
            {
                var parameter = caller.Name == "ForwardParameter";
                var proof = X64CallResultBooleanTailProof.Find(caller);
                Assert.That(proof, Is.Not.Null, caller.FullNameWithSignature);
                Assert.Multiple(() =>
                {
                    Assert.That(proof!.ParameterIndex, Is.EqualTo(parameter ? (int?)(caller.IsStatic ? 1 : 0) : null));
                    Assert.That(proof.LiteralValue, Is.EqualTo(caller.Name == "ForwardTrue"));
                    Assert.That(proof.ChecksProducerReceiver, Is.EqualTo(caller.IsStatic));
                    Assert.That(proof.Producer.Name, Is.EqualTo("GetNode"));
                    Assert.That(proof.Target.Name, Is.EqualTo("Apply"));
                });

                foreach (var method in new[] { caller, proof!.Producer, proof.Target })
                {
                    var flags = method.Definition!.iflags;
                    foreach (var invalid in new[] { (ushort)MethodImplAttributes.Synchronized, (ushort)0xF000 })
                    {
                        method.Definition.iflags = (ushort)(flags | invalid);
                        try { Assert.That(X64CallResultBooleanTailProof.Find(caller), Is.Null); }
                        finally { method.Definition.iflags = flags; }
                    }
                }
                var cached = caller.RawBytes;
                var mismatch = cached.AsSpan().ToArray();
                mismatch[0] ^= 1;
                caller.RawBytes = new BinarySlice(mismatch);
                try { Assert.That(X64CallResultBooleanTailProof.Find(caller), Is.Null); }
                finally { caller.RawBytes = cached; }
                var interior = caller.UnderlyingPointer + 1;
                app.MethodsByAddress[interior] = [caller];
                try { Assert.That(X64CallResultBooleanTailProof.Find(caller), Is.Null); }
                finally { app.MethodsByAddress.Remove(interior); }

                foreach (var target in new[] { proof.Producer, proof.Target })
                    RejectDisplacedTarget(caller, target);
                if (parameter)
                {
                    var boolean = caller.Parameters[proof.ParameterIndex!.Value];
                    boolean.OverrideParameterType = app.SystemTypes.SystemByteType;
                    try { Assert.That(X64CallResultBooleanTailProof.Find(caller), Is.Null); }
                    finally { boolean.OverrideParameterType = null; }
                }
                Assert.That(X64CallResultBooleanTailProof.Find(caller), Is.Not.Null);
            }

            void RejectDisplacedTarget(MethodAnalysisContext caller, MethodAnalysisContext target)
            {
                var field = target.Definition!.GetType().GetField("_methodPointer", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var pointer = field.GetValue(target.Definition);
                var displaced = target.UnderlyingPointer + 1;
                Assert.That(app.MethodsByAddress.ContainsKey(displaced), Is.False);
                app.MethodsByAddress[displaced] = [target];
                field.SetValue(target.Definition, displaced);
                try
                {
                    Assert.That(RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(target), Is.True);
                    Assert.That(X64CallResultBooleanTailProof.Find(caller), Is.Null,
                        "A valid callee binding at another address does not bind the caller's actual native destination.");
                }
                finally
                {
                    field.SetValue(target.Definition, pointer);
                    app.MethodsByAddress.Remove(displaced);
                }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
