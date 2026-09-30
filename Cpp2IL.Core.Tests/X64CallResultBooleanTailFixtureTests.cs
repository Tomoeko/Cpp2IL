using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional player controls for direct engine calls and their ordered null guards.</summary>
[NonParallelizable]
public class X64CallResultBooleanTailFixtureTests
{
    [TestCase("CPP2IL_CALL_RESULT_ENGINE_FALSE_TAIL_FIXTURE_INPUT",
        "CallResultEngineFalseTailFixture", "ActivityBehaviour", "DisableSelf", false)]
    [TestCase("CPP2IL_ENGINE_COMPONENT_FALSE_TAIL_FIXTURE_INPUT",
        "EngineComponentFalseTailFixture", "GuardedEngineCall", "Disable", true)]
    public void PlayerCallsRequireUnchangedUniqueCalleesAndBooleanParameter(
        string variable, string assemblyName, string typeName, string methodName,
        bool checksProducerReceiver)
    {
        var directory = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore($"Set {variable} to the neutral exact player input.");
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
            var method = app.GetAssemblyByName(assemblyName)!.Types
                .Single(type => type.Name == typeName).Methods
                .Single(candidate => candidate.Name == methodName);
            var body = X64CallResultBooleanTailProof.ReadBody(method);
            TestContext.Out.WriteLine($"Caller: {method.UnderlyingPointer:X}; cached bytes: {method.RawBytes.Length}");
            foreach (var native in body ?? X86Utils.Iterate(method).Take(13).ToArray())
                TestContext.Out.WriteLine($"{native.IP:X}: {native}");
            Assert.That(body, Has.Length.EqualTo(checksProducerReceiver ? 13 : 11));
            var evidence = X64CallResultBooleanTailProof.Find(method);
            Assert.That(evidence, Is.Not.Null,
                "The complete native body, guards and callee identities must all authenticate.");
            Assert.Multiple(() =>
            {
                Assert.That(evidence!.ChecksProducerReceiver, Is.EqualTo(checksProducerReceiver));
                Assert.That(evidence.Producer.DeclaringType!.FullName, Is.EqualTo("UnityEngine.Component"));
                Assert.That(evidence.Producer.Name, Is.EqualTo("get_gameObject"));
                Assert.That(evidence.Target.DeclaringType!.FullName, Is.EqualTo("UnityEngine.GameObject"));
                Assert.That(evidence.Target.Name, Is.EqualTo("SetActive"));
                Assert.That(method.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                    MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall),
                    Is.EqualTo(MethodImplAttributes.IL),
                    "The recovered caller is ordinary managed IL.");
                Assert.That(evidence.Producer.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                    MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall),
                    Is.EqualTo(MethodImplAttributes.InternalCall));
                Assert.That(evidence.Target.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                    MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall),
                    Is.EqualTo(MethodImplAttributes.InternalCall),
                    "Calls retain runtime-provided declarations instead of synthesizing their bodies.");
            });

            foreach (var target in new[] { method, evidence!.Producer, evidence.Target })
            {
                var definition = target.Definition!;
                var originalImplementation = definition.iflags;
                try
                {
                    definition.iflags |= 0xF000;
                    Assert.That(definition.IsUnmanagedCallersOnly, Is.True);
                    Assert.That(X64CallResultBooleanTailProof.Find(method), Is.Null,
                        "a complete extension marker cannot authenticate an ordinary managed caller or callee ABI");
                }
                finally { definition.iflags = originalImplementation; }
            }
            Assert.That(X64CallResultBooleanTailProof.Find(method), Is.Not.Null);

            var cachedBytes = method.RawBytes;
            var mismatch = cachedBytes.AsSpan().ToArray();
            mismatch[0] ^= 1;
            method.RawBytes = new BinarySlice(mismatch);
            try
            {
                Assert.That(X64CallResultBooleanTailProof.Find(method), Is.Null,
                    "The metadata slice must agree with the independently authenticated PE prefix.");
            }
            finally { method.RawBytes = cachedBytes; }

            foreach (var target in new[] { evidence!.Producer, evidence.Target })
            {
                var receiver = new LocalVariable("runtime-receiver",
                    new Register(1350, "runtime-receiver", 1), target.DeclaringType!);
                var arguments = new System.Collections.Generic.List<IOperand> { target };
                if (!target.IsVoid)
                    arguments.Add(new LocalVariable("runtime-result",
                        new Register(1351, "runtime-result", 1), target.ReturnType));
                arguments.Add(receiver);
                arguments.AddRange(target.Parameters.Select(_ => (IOperand)new Immediate(0)));
                arguments.Add(new Immediate(0));
                var call = new Instruction(0, target.IsVoid ? OpCode.CallVoid : OpCode.Call, arguments);
                Assert.That(NullCheckedCall.TryGet(call, out var bound, out var boundReceiver), Is.True,
                    "An unchanged uniquely bound InternalCall target retains its managed invocation ABI.");
                Assert.That(bound, Is.SameAs(target));
                Assert.That(boundReceiver, Is.SameAs(receiver));

                var implementation = target.Definition!.iflags;
                foreach (var invalid in new ushort[]
                {
                    (ushort)(MethodImplAttributes.InternalCall | MethodImplAttributes.Native),
                    (ushort)(MethodImplAttributes.InternalCall | MethodImplAttributes.OPTIL),
                    (ushort)(MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime),
                    (ushort)(MethodImplAttributes.InternalCall | MethodImplAttributes.Unmanaged),
                    (ushort)(MethodImplAttributes.InternalCall | MethodImplAttributes.NoInlining),
                    0x3000,
                    0xF000,
                })
                {
                    try
                    {
                        target.Definition.iflags = invalid;
                        Assert.That(target.ImplAttributes, Is.EqualTo(target.DefaultImplAttributes));
                        Assert.That(NullCheckedCall.TryGet(call, out _, out _), Is.False,
                            "Only the exact original InternalCall implementation class is admitted.");
                        Assert.That(X64CallResultBooleanTailProof.Find(method), Is.Null);
                    }
                    finally { target.Definition.iflags = implementation; }
                }

                var bindings = app.MethodsByAddress[target.UnderlyingPointer];
                bindings.Add(target);
                try
                {
                    Assert.That(NullCheckedCall.TryGet(call, out _, out _), Is.False,
                        "InternalCall eligibility must revalidate unique native ownership.");
                    Assert.That(X64CallResultBooleanTailProof.Find(method), Is.Null,
                        "A duplicate managed call identity does not establish a unique callee.");
                }
                finally { bindings.RemoveAt(bindings.Count - 1); }

                var bindingIndex = bindings.FindIndex(candidate => ReferenceEquals(candidate, target));
                bindings.RemoveAt(bindingIndex);
                try
                {
                    Assert.That(NullCheckedCall.TryGet(call, out _, out _), Is.False,
                        "A runtime-provided declaration without its original native binding is not eligible.");
                }
                finally { bindings.Insert(bindingIndex, target); }
                Assert.That(NullCheckedCall.TryGet(call, out _, out _), Is.True);

                if (target.Parameters.Count == 1)
                {
                    var pure = call.Operands[2];
                    call.SetOperand(2, new MemoryOperand(receiver));
                    try
                    {
                        Assert.That(NullCheckedCall.TryGet(call, out _, out _), Is.False,
                            "Runtime declarations do not allow effectful argument loads across the null failure.");
                    }
                    finally { call.SetOperand(2, pure); }
                }

                var name = target.OverrideName;
                target.OverrideName = "ChangedCallIdentity";
                try
                {
                    Assert.That(X64CallResultBooleanTailProof.Find(method), Is.Null);
                }
                finally { target.OverrideName = name; }
            }

            var parameter = evidence.Target.Parameters.Single();
            parameter.OverrideParameterType = app.SystemTypes.SystemByteType;
            try
            {
                Assert.That(X64CallResultBooleanTailProof.Find(method), Is.Null,
                    "The zero argument is an evidenced Boolean, not an arbitrary numeric parameter.");
            }
            finally { parameter.OverrideParameterType = null; }

            foreach (var type in new[] { method.DeclaringType!, evidence.Producer.DeclaringType!,
                         evidence.Producer.ReturnType, evidence.Target.DeclaringType! }.Distinct())
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
                        Assert.That(X64CallResultBooleanTailProof.Find(method), Is.Null,
                            $"A {flag} class descriptor cannot authenticate an ordinary reference receiver.");
                    }
                    finally { (raw.NumMods, raw.Byref, raw.Pinned) = previous; }
                }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
