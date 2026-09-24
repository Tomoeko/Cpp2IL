using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class CallResultNullGuardProofTests
{
    [Test]
    public void ExactPlayerRejectsChangedCallsiteAndAmbiguousManagedAliases()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_CALL_RESULT_GUARD_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CALL_RESULT_GUARD_FIXTURE_INPUT to the neutral exact player-input directory.");

        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data",
            "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName("CallResultNullGuardFixture")!;
            var root = assembly.Types.Single(type => type.Name == "ChainRoot");
            var node = assembly.Types.Single(type => type.Name == "ChainNode");
            var baseType = assembly.Types.Single(type =>
                type.Name == "InheritedFlagBase");
            var getter = root.Methods.Single(method => method.Name == "GetFirst");
            var repeatedGetter = root.Methods.Single(method => method.Name ==
                "GetRepeated");
            var next = node.Methods.Single(method => method.Name == "GetNext");
            var read = node.Methods.Single(method => method.Name == "Read");
            var readWith = node.Methods.Single(method => method.Name ==
                "ReadWith");
            var setter = baseType.Methods.Single(method => method.Name ==
                "set_Enabled");
            var staticAlias = app.GetAssemblyByName("RecoveryValidation.Runtime")!
                .Types.SelectMany(type => type.Methods)
                .First(method => method.IsStatic && method.UnderlyingPointer != 0);

            var execute = root.Methods.Single(method => method.Name == "Execute");
            execute.Analyze();
            var calls = execute.ControlFlowGraph!.Instructions
                .Where(instruction => instruction.IsCall).ToArray();
            var origin = calls.Single(instruction =>
                ReferenceEquals(instruction.Operands[0], getter));
            var guarded = calls.Single(instruction =>
                ReferenceEquals(instruction.Operands[0], next));
            var result = (LocalVariable)origin.Destination!;
            Assert.That(CallResultNullGuardProof.HasBoundTarget(execute, result,
                origin, guarded, next), Is.True);
            var copiedResult = new LocalVariable("copiedResult",
                new Register(999, "copiedResult", 1), result.Type);
            var definitions = new Dictionary<LocalVariable, Instruction>
            {
                [result] = origin,
                [copiedResult] = new Instruction(-1, OpCode.Move,
                    copiedResult, result),
            };
            Assert.That(RuntimeNullGuardCoalescer.TraceReceiverOrigin(result,
                local => definitions.GetValueOrDefault(local), _ => false,
                out _),
                Is.EqualTo(RuntimeNullGuardCoalescer.ReceiverOrigin.DirectCallResult));
            Assert.That(RuntimeNullGuardCoalescer.TraceReceiverOrigin(copiedResult,
                local => definitions.GetValueOrDefault(local), _ => false,
                out var copiedOrigin),
                Is.EqualTo(RuntimeNullGuardCoalescer.ReceiverOrigin.CopiedCallResult));
            Assert.That(copiedOrigin, Is.SameAs(origin));
            Assert.That(CallResultNullGuardProof.HasBoundTarget(execute,
                copiedResult, origin, guarded, next), Is.False);
            definitions[copiedResult] = new Instruction(-1, OpCode.Phi,
                copiedResult, result);
            Assert.That(RuntimeNullGuardCoalescer.TraceReceiverOrigin(copiedResult,
                local => definitions.GetValueOrDefault(local), _ => false,
                out _),
                Is.EqualTo(RuntimeNullGuardCoalescer.ReceiverOrigin.InvalidCopyChain));
            definitions[copiedResult] = new Instruction(-1, OpCode.Move,
                copiedResult, copiedResult);
            Assert.That(RuntimeNullGuardCoalescer.TraceReceiverOrigin(copiedResult,
                local => definitions.GetValueOrDefault(local), _ => false,
                out _),
                Is.EqualTo(RuntimeNullGuardCoalescer.ReceiverOrigin.InvalidCopyChain));
            definitions[copiedResult] = new Instruction(-1, OpCode.Move,
                copiedResult, result) { IntegerBitWidth = 32 };
            Assert.That(RuntimeNullGuardCoalescer.TraceReceiverOrigin(copiedResult,
                local => definitions.GetValueOrDefault(local), _ => false,
                out _),
                Is.EqualTo(RuntimeNullGuardCoalescer.ReceiverOrigin.InvalidCopyChain));
            definitions[copiedResult] = new Instruction(-1, OpCode.Move,
                copiedResult);
            Assert.That(RuntimeNullGuardCoalescer.TraceReceiverOrigin(copiedResult,
                local => definitions.GetValueOrDefault(local), _ => false,
                out _),
                Is.EqualTo(RuntimeNullGuardCoalescer.ReceiverOrigin.InvalidCopyChain));
            definitions.Remove(result);
            definitions[copiedResult] = new Instruction(-1, OpCode.Move,
                copiedResult, result);
            Assert.That(RuntimeNullGuardCoalescer.TraceReceiverOrigin(copiedResult,
                local => definitions.GetValueOrDefault(local), _ => false,
                out _),
                Is.EqualTo(RuntimeNullGuardCoalescer.ReceiverOrigin.InvalidCopyChain));
            Assert.That(RuntimeNullGuardCoalescer.TraceReceiverOrigin(copiedResult,
                local => definitions.GetValueOrDefault(local),
                local => ReferenceEquals(local, result), out _),
                Is.EqualTo(RuntimeNullGuardCoalescer.ReceiverOrigin.Other),
                "a copied managed parameter retains the established path");

            var callsite = guarded.NativeAddress;
            guarded.NativeAddress = origin.NativeAddress;
            try
            {
                Assert.That(CallResultNullGuardProof.HasBoundTarget(execute,
                    result, origin, guarded, next), Is.False,
                    "a different native callsite cannot bind this invocation");
            }
            finally { guarded.NativeAddress = callsite; }

            var nextAliases = app.MethodsByAddress[next.UnderlyingPointer];
            RejectAlias(nextAliases, staticAlias, () =>
                CallResultNullGuardProof.HasBoundTarget(execute, result,
                    origin, guarded, next));
            RejectAlias(nextAliases, read, () =>
                CallResultNullGuardProof.HasBoundTarget(execute, result,
                    origin, guarded, next));

            var inherited = root.Methods.Single(method => method.Name ==
                "ExecuteInheritedRepeated");
            inherited.Analyze();
            var inheritedCalls = inherited.ControlFlowGraph!.Instructions
                .Where(instruction => instruction.IsCall).ToArray();
            var secondGetter = inheritedCalls.Last(instruction =>
                ReferenceEquals(instruction.Operands[0], repeatedGetter));
            var finalRead = inheritedCalls.Single(instruction =>
                ReferenceEquals(instruction.Operands[0], readWith));
            var secondResult = (LocalVariable)secondGetter.Destination!;
            Assert.That(app.MethodsByAddress[readWith.UnderlyingPointer],
                Has.Count.EqualTo(1), "the guarded tail target is unique");
            Assert.That(CallResultNullGuardProof.HasBoundTarget(inherited,
                secondResult, secondGetter, finalRead, readWith), Is.True);
            RejectAlias(app.MethodsByAddress[repeatedGetter.UnderlyingPointer],
                staticAlias, () => CallResultNullGuardProof.HasBoundTarget(
                    inherited, secondResult, secondGetter, finalRead, readWith));
            Assert.That(inherited.InlinedBooleanSetters, Has.Count.EqualTo(1));
            var evidence = inherited.InlinedBooleanSetters.Single();
            Assert.That(evidence.IsValidFor(inherited), Is.True);
            var storeAddress = evidence.Call.NativeAddress;
            evidence.Call.NativeAddress = evidence.EarlierRead.Operation.NativeAddress;
            try
            {
                Assert.That(evidence.IsValidFor(inherited), Is.False,
                    "a field read cannot stand in for the native byte store");
            }
            finally { evidence.Call.NativeAddress = storeAddress; }
            var setterAliases = app.MethodsByAddress[setter.UnderlyingPointer];
            RejectAlias(setterAliases, staticAlias,
                () => evidence.IsValidFor(inherited));
            RejectAlias(setterAliases, read,
                () => evidence.IsValidFor(inherited));

            var field = evidence.NativeAccess.Field;
            field.OverrideOffset = field.DefaultOffset + 1;
            try
            {
                Assert.That(evidence.IsValidFor(inherited), Is.False,
                    "a changed private backing-field layout is unresolved");
            }
            finally { field.OverrideOffset = null; }
            Assert.That(evidence.IsValidFor(inherited), Is.True);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectAlias(
        System.Collections.Generic.List<Cpp2IL.Core.Model.Contexts.MethodAnalysisContext> aliases,
        Cpp2IL.Core.Model.Contexts.MethodAnalysisContext alias,
        Func<bool> proved)
    {
        aliases.Add(alias);
        try { Assert.That(proved(), Is.False); }
        finally { aliases.RemoveAt(aliases.Count - 1); }
        Assert.That(proved(), Is.True);
    }
}
