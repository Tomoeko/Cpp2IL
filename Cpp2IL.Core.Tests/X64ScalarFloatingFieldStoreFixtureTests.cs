using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional computed floating-store path and native-origin regressions.</summary>
[NonParallelizable]
public class X64ScalarFloatingFieldStoreFixtureTests
{
    [Test]
    public void ExactComputedStoreRetainsComparisonPathsReceiverValueAndEffects()
    {
        WithFixture(method =>
        {
            method.Analyze();
            var graph = method.ControlFlowGraph!;
            var operation = graph.Instructions.Single(instruction =>
                instruction.Operands is [FieldReference, LocalVariable]);
            var access = (FieldReference)operation.Operands[0];
            Assert.That(X64ScalarFloatingFieldStoreProof.HasRecord(method, operation), Is.True);
            Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.True);
            Assert.That(method.NullCheckedFieldAccesses, Has.Count.EqualTo(1));
            Assert.That(method.NullCheckedFieldAccesses[0].IsValidFor(method), Is.True);

            var value = operation.Operands[1];
            try
            {
                operation.SetOperand(1, new LocalVariable("substituted-value", new Register(7000, "substituted-value"), access.Field.FieldType));
                Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.False);
                operation.SetOperand(1, new DoubleLiteral(0));
                Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.False,
                    "The parameter-taking comparison path cannot be replaced by a constant.");
            }
            finally { operation.SetOperand(1, value); }

            var receiver = access.Local;
            try
            {
                access.Local = new LocalVariable("substituted-receiver", new Register(7001, "substituted-receiver"), receiver.Type);
                Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.False);
            }
            finally { access.Local = receiver; }

            var branch = graph.Instructions.Single(instruction => instruction.OpCode == OpCode.ConditionalJump);
            var condition = branch.Operands[1];
            try
            {
                branch.SetOperand(1, new Immediate(1));
                Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.False,
                    "An unchanged branch address does not authenticate a changed condition.");
            }
            finally { branch.SetOperand(1, condition); }

            var comparison = graph.Instructions.First(instruction => instruction.OpCode == OpCode.FloatCompare);
            var originalLeft = comparison.Operands[1];
            try
            {
                comparison.SetOperand(1, comparison.Operands[2]);
                Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.False);
            }
            finally { comparison.SetOperand(1, originalLeft); }

            var storeBlock = graph.FindBlockByInstruction(operation)!;
            var injected = new Instruction(-1, OpCode.Throw, new Immediate(0));
            storeBlock.Instructions.Insert(storeBlock.Instructions.IndexOf(operation), injected);
            try { Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.False); }
            finally { storeBlock.Instructions.Remove(injected); }
            Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.True);
            var rawBase = method.DeclaringType!.Definition!.RawBaseType!;
            var pinned = rawBase.Pinned;
            try
            {
                rawBase.Pinned = 1;
                Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.False,
                    "The resolved base context does not authenticate a changed original base descriptor.");
            }
            finally { rawBase.Pinned = pinned; }
        });
    }

    [Test]
    public void ClearedMutableAdmissionCannotBypassFinalIlValidation()
    {
        WithFixture(method =>
        {
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(method.AppContext);
            method.Analyze();
            Assert.That(X64ScalarFloatingFieldStoreProof.HasEvidence(method), Is.True);
            Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method), Is.True);
            var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
            method.PutExtraData<object>(X64ScalarFloatingFieldStoreProof.EvidenceKey, null!);
            method.NullCheckedFieldAccesses.Clear();
            Assert.That(X64ScalarFloatingFieldStoreProof.HasEvidence(method), Is.True);
            Assert.That(() => IlGenerator.GenerateIl(method, definition),
                Throws.TypeOf<DecompilerException>().With.Message.Contains("Floating field store"));
        });
    }

    [Test]
    public void ExactComputedStoreRequiresUnchangedMetadataLayoutAndNativeCache()
    {
        WithFixture(method =>
        {
            method.Analyze();
            var operation = method.ControlFlowGraph!.Instructions.Single(instruction =>
                instruction.Operands is [FieldReference, LocalVariable]);
            var access = (FieldReference)operation.Operands[0];
            Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.True);
            var field = access.Field;
            var attributes = field.OverrideAttributes;
            var offset = field.OverrideOffset;
            var type = field.OverrideFieldType;
            try
            {
                field.Attributes |= FieldAttributes.InitOnly;
                Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.False);
                field.OverrideAttributes = attributes;
                field.Offset += 8;
                Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.False);
                field.OverrideOffset = offset;
                field.FieldType = method.AppContext.SystemTypes.SystemSingleType;
                Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.False);
            }
            finally { field.OverrideAttributes = attributes; field.OverrideOffset = offset; field.OverrideFieldType = type; }

            var cache = method.RawBytes;
            try
            {
                var altered = cache.ToArray();
                altered[0] ^= 1;
                method.RawBytes = new BinarySlice(altered);
                Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.False);
            }
            finally { method.RawBytes = cache; }
            var address = operation.NativeAddress;
            try
            {
                operation.NativeAddress = address + 1;
                Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.False);
            }
            finally { operation.NativeAddress = address; }
            Assert.That(X64ScalarFloatingFieldStoreProof.IsValidFor(method, operation, access), Is.True);
        });
    }

    private static void WithFixture(Action<MethodAnalysisContext> action)
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_SCALAR_FLOAT_SELECTION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_SCALAR_FLOAT_SELECTION_FIXTURE_INPUT to the neutral scalar player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
                Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            action(Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("ScalarFloatSelectionFixture")!.Types
                .SelectMany(type => type.Methods).Single(method => method.Name == "StorePositive64"));
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
