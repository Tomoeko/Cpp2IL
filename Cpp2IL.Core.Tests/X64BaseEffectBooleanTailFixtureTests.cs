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

/// <summary>Optional player controls for declared base dispatch and folded Boolean tails.</summary>
[NonParallelizable]
public class X64BaseEffectBooleanTailFixtureTests
{
    [TestCase("EnableSequence", true)]
    [TestCase("DisableSequence", false)]
    public void CompletePlayerSequenceRequiresOriginalAncestorsAndExactNativeCallBindings(
        string typeName, bool value)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_BASE_EFFECT_BOOLEAN_TAIL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_BASE_EFFECT_BOOLEAN_TAIL_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName("BaseEffectBooleanTailFixture")!;
            var owner = assembly.Types.Single(type => type.Name == typeName);
            var caller = owner.Methods.Single(method => method.Name == "Apply");
            var body = X64BaseEffectBooleanTailProof.ReadBody(caller);
            foreach (var instruction in body ?? X86Utils.Iterate(caller).Take(17).ToArray())
                TestContext.Out.WriteLine($"{instruction.IP:X}: {instruction}");
            var proof = X64BaseEffectBooleanTailProof.Find(caller);
            Assert.That(proof, Is.Not.Null);
            var other = assembly.Types.Single(type => type.Name == "FoldedTarget").Methods
                .Single(method => method.Name == "set_Enabled");
            Assert.Multiple(() =>
            {
                Assert.That(caller.IsVirtual, Is.True);
                Assert.That(proof!.Value, Is.EqualTo(value));
                Assert.That(proof.Effect.IsVirtual, Is.True);
                Assert.That(proof.Effect.DeclaringType!.Name, Is.EqualTo("SequenceBase"));
                Assert.That(proof.Effect.Name, Is.EqualTo("Apply"));
                Assert.That(proof.Producer.DeclaringType, Is.SameAs(proof.Effect.DeclaringType));
                Assert.That(proof.Producer.Name, Is.EqualTo("GetTarget"));
                Assert.That(proof.Target.DeclaringType!.Name, Is.EqualTo("SequenceTarget"));
                Assert.That(proof.Target.Name, Is.EqualTo("set_Enabled"));
                Assert.That(proof.Target.UnderlyingPointer, Is.EqualTo(other.UnderlyingPointer),
                    "The neutral fixture must actually exercise identical native setter folding.");
                Assert.That(proof.FoldedSetterField?.Name, Is.EqualTo("Flag"));
            });

            var originalBase = owner.OverrideBaseType;
            owner.OverrideBaseType = app.SystemTypes.SystemObjectType;
            try { Assert.That(X64BaseEffectBooleanTailProof.Find(caller), Is.Null); }
            finally { owner.OverrideBaseType = originalBase; }

            foreach (var method in new[] { caller, proof!.Effect, proof.Producer, proof.Target })
            {
                var aliases = app.MethodsByAddress[method.UnderlyingPointer];
                aliases.Add(method);
                try { Assert.That(X64BaseEffectBooleanTailProof.Find(caller), Is.Null,
                    "Duplicate original identities must not authenticate a caller or callee."); }
                finally { aliases.RemoveAt(aliases.Count - 1); }
                var flags = method.Definition!.iflags;
                method.Definition.iflags |= 0xF000;
                try { Assert.That(X64BaseEffectBooleanTailProof.Find(caller), Is.Null); }
                finally { method.Definition.iflags = flags; }
            }

            // Preserve the original own-address binding while injecting a
            // displaced lookup. A dictionary hit alone is not call evidence.
            foreach (var method in new[] { proof.Effect, proof.Producer, proof.Target })
                RejectDisplacedBinding(method);

            var field = proof.FoldedSetterField!;
            var offset = field.OverrideOffset;
            field.OverrideOffset = field.DeclaringType!.Fields.Single(candidate => candidate.Name == "Neighbor").Offset;
            try { Assert.That(X64BaseEffectBooleanTailProof.Find(caller), Is.Null); }
            finally { field.OverrideOffset = offset; }

            foreach (var type in new[] { owner, proof.Effect.DeclaringType!, proof.Producer.ReturnType })
            {
                var raw = type.Definition!.RawType!;
                var previous = raw.NumMods;
                raw.NumMods = 1;
                try { Assert.That(X64BaseEffectBooleanTailProof.Find(caller), Is.Null); }
                finally { raw.NumMods = previous; }
            }

            var rawBase = owner.Definition!.RawBaseType!;
            var originalBaseModifiers = rawBase.NumMods;
            rawBase.NumMods = 1;
            try { Assert.That(X64BaseEffectBooleanTailProof.Find(caller), Is.Null,
                "Resolving to a class context does not authenticate a modified base descriptor."); }
            finally { rawBase.NumMods = originalBaseModifiers; }

            foreach (var method in new[] { caller, proof.Target })
            {
                var bytes = method.RawBytes;
                var altered = bytes.AsSpan().ToArray();
                altered[0] ^= 1;
                method.RawBytes = new BinarySlice(altered);
                try { Assert.That(X64BaseEffectBooleanTailProof.Find(caller), Is.Null,
                    "Cached caller and independently bound folded-setter bytes must match the PE."); }
                finally { method.RawBytes = bytes; }
            }
            Assert.That(X64BaseEffectBooleanTailProof.Find(caller), Is.Not.Null);

            void RejectDisplacedBinding(MethodAnalysisContext method)
            {
                var pointer = method.Definition!.GetType().GetField("_methodPointer",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                var originalPointer = pointer.GetValue(method.Definition);
                var displaced = method.UnderlyingPointer + 1;
                Assert.That(app.MethodsByAddress.ContainsKey(displaced), Is.False);
                // The original lookup remains present while the selected method
                // also receives a valid own-address binding somewhere else.
                app.MethodsByAddress[displaced] = [method];
                pointer.SetValue(method.Definition, displaced);
                try
                {
                    Assert.That(RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method,
                        requireUniqueBinding: false), Is.True);
                    Assert.That(X64BaseEffectBooleanTailProof.Find(caller), Is.Null,
                        "Own-address eligibility does not bind a different native call destination.");
                }
                finally
                {
                    pointer.SetValue(method.Definition, originalPointer);
                    app.MethodsByAddress.Remove(displaced);
                }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
