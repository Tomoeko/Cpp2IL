using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional exact-player evidence for original inherited field-call identities.</summary>
[NonParallelizable]
public class X64InternalCallFieldFixtureTests
{
    [Test]
    public void InheritedStringSetterRequiresOriginalBaseFieldsAndUniqueNativeTarget()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_INTERNAL_CALL_FIELD_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_INTERNAL_CALL_FIELD_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var holder = app.GetAssemblyByName("InternalCallFieldFixture")!.Types
                .Single(type => type.Name == "TargetHolder");
            var method = holder.Methods.Single(candidate => candidate.Name == "Rename");
            var receiver = holder.Fields.Single(field => field.Name == "Target");
            var label = holder.Fields.Single(field => field.Name == "Label");
            var evidence = X64GuardedFieldCallProof.Find(method);
            Assert.That(evidence, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(evidence!.ReceiverField, Is.SameAs(receiver));
                Assert.That(evidence.ArgumentFields, Is.EqualTo(new[] { label }));
                Assert.That(evidence.Target.DeclaringType!.FullName, Is.EqualTo("UnityEngine.Object"));
                Assert.That(evidence.Target.Name, Is.EqualTo("set_name"));
                Assert.That(evidence.Target.Parameters.Single().ParameterType,
                    Is.SameAs(app.SystemTypes.SystemStringType));
                Assert.That(receiver.FieldType.FullName, Is.EqualTo("UnityEngine.GameObject"));
                Assert.That(receiver.FieldType, Is.Not.SameAs(evidence.Target.DeclaringType));
            });
            var read = holder.Methods.Single(candidate => candidate.Name == "ReadActive");
            Assert.That(X64GuardedFieldCallProof.Find(read)?.Target.Name, Is.EqualTo("get_activeSelf"));
            TestContext.Out.WriteLine($"Inherited caller: {method.UnderlyingPointer:X}; target: {evidence!.Target.UnderlyingPointer:X}");

            var originalBase = receiver.FieldType.OverrideBaseType;
            receiver.FieldType.OverrideBaseType = app.SystemTypes.SystemObjectType;
            try
            {
                Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                    "The native receiver must retain the original declaring-base path.");
            }
            finally { receiver.FieldType.OverrideBaseType = originalBase; }

            foreach (var type in new[] { holder, receiver.FieldType, evidence.Target.DeclaringType! })
            {
                var raw = type.Definition!.RawType!;
                foreach (var flag in new[] { "modifier", "byref", "pinned" })
                {
                    var previous = (raw.NumMods, raw.Byref, raw.Pinned);
                    if (flag == "modifier") raw.NumMods = 1;
                    else if (flag == "byref") raw.Byref = 1;
                    else raw.Pinned = 1;
                    try
                    {
                        Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                            $"A {flag} descriptor cannot authenticate this class receiver.");
                    }
                    finally { (raw.NumMods, raw.Byref, raw.Pinned) = previous; }
                }
            }

            var parameter = evidence.Target.Parameters.Single();
            var parameterType = parameter.OverrideParameterType;
            parameter.OverrideParameterType = app.SystemTypes.SystemObjectType;
            try
            {
                Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                    "The loaded string field must have the original parameter type.");
            }
            finally { parameter.OverrideParameterType = parameterType; }

            var labelOffset = label.OverrideOffset;
            label.OverrideOffset = holder.Fields.Single(field => field.Name == "Neighbor").Offset;
            try
            {
                Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                    "A different field offset cannot supply the original string argument.");
            }
            finally { label.OverrideOffset = labelOffset; }

            var bindings = app.MethodsByAddress[evidence.Target.UnderlyingPointer];
            bindings.Add(evidence.Target);
            try
            {
                Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                    "Two managed owners do not authenticate the direct call identity.");
            }
            finally { bindings.RemoveAt(bindings.Count - 1); }

            var originalBytes = method.RawBytes;
            var changedBytes = originalBytes.AsSpan().ToArray();
            changedBytes[0] ^= 1;
            method.RawBytes = new BinarySlice(changedBytes);
            try
            {
                Assert.That(X64GuardedFieldCallProof.Find(method), Is.Null,
                    "Cached native bytes must match the complete executable PE body.");
            }
            finally { method.RawBytes = originalBytes; }
            Assert.That(X64GuardedFieldCallProof.Find(method), Is.Not.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
