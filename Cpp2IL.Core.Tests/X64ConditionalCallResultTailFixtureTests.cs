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
public class X64ConditionalCallResultTailFixtureTests
{
    [Test]
    public void ConditionalCallsRequireOriginalBodiesSignaturesAndNativeDestinations()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_CONDITIONAL_CALL_RESULT_TAIL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CONDITIONAL_CALL_RESULT_TAIL_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var predicate = app.GetAssemblyByName("ConditionalCallResultTailFixture")!.Types
                .SelectMany(type => type.Methods).Single(method => method.Name == "ReadFlag");
            CheckEffectfulBooleanRead(predicate);
            var callers = app.GetAssemblyByName("ConditionalCallResultTailFixture")!.Types.SelectMany(type => type.Methods)
                .Where(method => method.Name.StartsWith("When", StringComparison.Ordinal)).ToArray();
            Assert.That(callers, Has.Length.EqualTo(5));
            foreach (var caller in callers)
            {
                var proof = X64ConditionalCallResultTailProof.Find(caller);
                Assert.That(proof, Is.Not.Null, caller.FullNameWithSignature);
                Assert.Multiple(() =>
                {
                    Assert.That(caller.IsVirtual, Is.True);
                    Assert.That(proof!.Producer.Visibility, Is.EqualTo(MethodAttributes.Family));
                    Assert.That(proof.Predicate.Name, Is.EqualTo("ReadFlag"));
                    Assert.That(proof.CallsWhenTrue, Is.EqualTo(!caller.Name.Contains("Not", StringComparison.Ordinal)));
                    Assert.That(proof.LiteralValue, Is.EqualTo(caller.Name.EndsWith("True", StringComparison.Ordinal)));
                    Assert.That(proof.Target.Name, Is.EqualTo(caller.Name.EndsWith("Zero", StringComparison.Ordinal)
                        ? "ApplyInteger" : "ApplyBoolean"));
                });
                foreach (var target in new[] { caller, proof!.Producer, proof.Predicate, proof.Target })
                {
                    var flags = target.Definition!.iflags;
                    foreach (var unsupported in new[] { (ushort)MethodImplAttributes.Synchronized,
                                 (ushort)MethodImplAttributes.InternalCall, (ushort)0xF000 })
                    {
                        target.Definition.iflags = (ushort)(flags | unsupported);
                        try { Assert.That(X64ConditionalCallResultTailProof.Find(caller), Is.Null); }
                        finally { target.Definition.iflags = flags; }
                    }
                    var name = target.Name;
                    target.Name = "ChangedMethod";
                    try { Assert.That(X64ConditionalCallResultTailProof.Find(caller), Is.Null); }
                    finally { target.Name = name; }
                }
                var bindings = app.MethodsByAddress[caller.UnderlyingPointer];
                app.MethodsByAddress[caller.UnderlyingPointer] = bindings.Concat(new[] { caller }).ToList();
                try { Assert.That(X64ConditionalCallResultTailProof.Find(caller), Is.Null); }
                finally { app.MethodsByAddress[caller.UnderlyingPointer] = bindings; }
                app.MethodsByAddress[caller.UnderlyingPointer] = [proof.Predicate];
                try { Assert.That(X64ConditionalCallResultTailProof.Find(caller), Is.Null); }
                finally { app.MethodsByAddress[caller.UnderlyingPointer] = bindings; }

                var owner = caller.DeclaringType!;
                var ownerName = owner.Name;
                owner.Name = "ChangedOwner";
                try { Assert.That(X64ConditionalCallResultTailProof.Find(caller), Is.Null); }
                finally { owner.Name = ownerName; }
                var originalBase = owner.BaseType;
                owner.BaseType = proof.Producer.ReturnType;
                try { Assert.That(X64ConditionalCallResultTailProof.Find(caller), Is.Null); }
                finally { owner.BaseType = originalBase; }
                var attributes = owner.Attributes;
                owner.Attributes ^= TypeAttributes.Sealed;
                try { Assert.That(X64ConditionalCallResultTailProof.Find(caller), Is.Null); }
                finally { owner.Attributes = attributes; }

                var cache = caller.RawBytes;
                var changed = cache.AsSpan().ToArray();
                changed[0] ^= 1;
                caller.RawBytes = new BinarySlice(changed);
                try { Assert.That(X64ConditionalCallResultTailProof.Find(caller), Is.Null); }
                finally { caller.RawBytes = cache; }
                var interior = caller.UnderlyingPointer + 1;
                app.MethodsByAddress[interior] = [caller];
                try { Assert.That(X64ConditionalCallResultTailProof.Find(caller), Is.Null); }
                finally { app.MethodsByAddress.Remove(interior); }
                proof.Predicate.OverrideReturnType = app.SystemTypes.SystemInt32Type;
                try { Assert.That(X64ConditionalCallResultTailProof.Find(caller), Is.Null); }
                finally { proof.Predicate.OverrideReturnType = null; }
                proof.Target.Parameters[0].OverrideParameterType = app.SystemTypes.SystemByteType;
                try { Assert.That(X64ConditionalCallResultTailProof.Find(caller), Is.Null); }
                finally { proof.Target.Parameters[0].OverrideParameterType = null; }
                foreach (var target in new[] { proof.Producer, proof.Predicate, proof.Target })
                    RejectDisplacedTarget(caller, target);
                Assert.That(X64ConditionalCallResultTailProof.Find(caller), Is.Not.Null);
            }

            void CheckEffectfulBooleanRead(MethodAnalysisContext method)
            {
                method.EnsureRawBytes();
                var body = X86Utils.Iterate(method).ToArray();
                var read = X86BooleanFieldReadProof.Find(method, body);
                Assert.Multiple(() =>
                {
                    Assert.That(read, Is.Not.Null);
                    Assert.That(read!.Field.Name, Is.EqualTo("Ready"));
                    Assert.That(read.LoadIp, Is.EqualTo(body[1].IP));
                    Assert.That(body[0].Code, Is.EqualTo(Iced.Intel.Code.Inc_rm32));
                });
                var cached = method.RawBytes;
                var changed = cached.AsSpan().ToArray();
                changed[2] ^= 4;
                method.RawBytes = new BinarySlice(changed);
                try { Assert.That(X86BooleanFieldReadProof.Find(method, body), Is.Null); }
                finally { method.RawBytes = cached; }
                var interior = body[1].IP;
                app.MethodsByAddress[interior] = [method];
                try { Assert.That(X86BooleanFieldReadProof.Find(method, body), Is.Null); }
                finally { app.MethodsByAddress.Remove(interior); }
                var flags = method.Definition!.iflags;
                method.Definition.iflags |= (ushort)MethodImplAttributes.Synchronized;
                try { Assert.That(X86BooleanFieldReadProof.Find(method, body), Is.Null); }
                finally { method.Definition.iflags = flags; }
                var counter = method.DeclaringType!.Fields.Single(field => field.Name == "PredicateCount");
                var counterAttributes = counter.BackingData!.Attributes;
                counter.BackingData.Attributes |= FieldAttributes.InitOnly;
                try
                {
                    Assert.That(counter.Attributes, Is.EqualTo(counter.DefaultAttributes),
                        "The original readonly declaration must fail even without a declaration override.");
                    Assert.That(X86BooleanFieldReadProof.Find(method, body), Is.Null);
                }
                finally { counter.BackingData.Attributes = counterAttributes; }
                counter.OverrideFieldType = app.SystemTypes.SystemUInt32Type;
                try { Assert.That(X86BooleanFieldReadProof.Find(method, body), Is.Null); }
                finally { counter.OverrideFieldType = null; }
                var fieldOffset = read!.Field.Offset;
                read.Field.Offset = counter.Offset;
                try { Assert.That(X86BooleanFieldReadProof.Find(method, body), Is.Null); }
                finally { read.Field.Offset = fieldOffset; }
                Assert.That(X86BooleanFieldReadProof.Find(method, body), Is.Not.Null);
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
                    Assert.That(X64ConditionalCallResultTailProof.Find(caller), Is.Null,
                        "A valid original signature at another pointer does not authenticate this native destination.");
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
