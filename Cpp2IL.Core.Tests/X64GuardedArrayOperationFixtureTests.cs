using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using OpCode = Cpp2IL.Core.ISIL.OpCode;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional player-only provenance and final graph gates for composed array guards.</summary>
[NonParallelizable]
public class X64GuardedArrayOperationFixtureTests
{
    [TestCase("ReadTwiceAfterEffect", 2)]
    [TestCase("TouchNode", 1)]
    [TestCase("ReadThenSet", 2)]
    [TestCase("Sum", 2)]
    public void ExactBodyRequiresOriginalOriginsAndCompleteOrderedTypedOperations(string name, int count)
    {
        WithFixture(app =>
        {
            var method = Method(app, name);
            var decoded = X86Utils.Iterate(method).ToArray();
            var evidence = X64GuardedArrayOperationProof.Find(method, decoded);
            Assert.That(evidence, Is.Not.Null);
            Assert.That(evidence!.Sites, Has.Count.EqualTo(count));
            var native = evidence.Body.ToArray();
            var shape = X64GuardedArrayOperationProof.TryProveNative(native,
                target => X86RuntimeNullThrowProof.TryIdentify(app, target) != null,
                target => X86RuntimeBoundsThrowProof.TryIdentify(app, target));
            Assert.That(shape, Is.Not.Null);
            if (name == "ReadTwiceAfterEffect")
            {
                Assert.That(evidence.Sites.Select(site => site.OffsetPreparationIp).Distinct().ToArray(), Has.Length.EqualTo(1));
                Assert.That(evidence.Sites[0].ArrayOrigin.FieldReadIp,
                    Is.Not.EqualTo(evidence.Sites[1].ArrayOrigin.FieldReadIp));
                var offset = native.ToArray();
                var preparation = shape!.Sites[0].OffsetPreparation!.Value;
                offset[preparation].MemoryDisplacement64++;
                Assert.That(X64GuardedArrayOperationProof.TryProveNative(offset, _ => true, _ => true), Is.Null);
            }
            if (name == "ReadThenSet")
                Assert.That(evidence.Sites[1].NullTestIp, Is.Null,
                    "The second store uses the captured array already proved nonnull by the first read.");

            var originalBytes = method.RawBytes;
            try
            {
                var changedBytes = originalBytes.ToArray();
                changedBytes[0] ^= 1;
                method.RawBytes = new BinarySlice(changedBytes);
                Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Null,
                    "Existing cached bytes must correlate with independent PE evidence.");
            }
            finally { method.RawBytes = originalBytes; }

            foreach (var field in evidence.Sites.Select(site => site.ArrayOrigin.Field)
                         .OfType<FieldAnalysisContext>().Distinct())
            {
                try
                {
                    field.OverrideOffset = field.DefaultOffset + 8;
                    Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Null);
                }
                finally { field.OverrideOffset = null; }
            }
            var parameter = method.Parameters[0];
            try
            {
                parameter.OverrideParameterType = app.SystemTypes.SystemInt64Type;
                Assert.That(X64GuardedArrayOperationProof.Find(method, decoded), Is.Null);
            }
            finally { parameter.OverrideParameterType = null; }

            method.Analyze();
            IlGenerator.ValidateGuardedArrayOperations(method);
            var graph = method.ControlFlowGraph!;
            var access = graph.Instructions.Single(instruction =>
                instruction.NativeAddress == evidence.Sites[0].OperationIp &&
                instruction.Operands.Any(operand => operand is ArrayAccess));
            var block = graph.FindBlockByInstruction(access)!;
            var position = block.Instructions.IndexOf(access);
            try
            {
                block.Instructions.RemoveAt(position);
                Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
            }
            finally { block.Instructions.Insert(position, access); }
            IlGenerator.ValidateGuardedArrayOperations(method);

            var injected = new Instruction(-1, OpCode.NewArr,
                new LocalVariable("unproved-array", new ISIL.Register(null, "new"),
                    access.Operands.OfType<ArrayAccess>().Single().Array.Type!),
                app.SystemTypes.SystemInt32Type, new Immediate(1));
            try
            {
                block.Instructions.Insert(position, injected);
                Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
            }
            finally { block.Instructions.Remove(injected); }

            if (evidence.Sites.FirstOrDefault(site => site.IsStore) is { } store)
            {
                var operation = graph.Instructions.Single(instruction => instruction.NativeAddress == store.OperationIp &&
                    instruction.Operands is [ArrayAccess, _]);
                var originalValue = operation.Operands[1];
                try
                {
                    operation.SetOperand(1, new Immediate(123));
                    Assert.Throws<DecompilerException>(() => IlGenerator.ValidateGuardedArrayOperations(method));
                }
                finally { operation.SetOperand(1, originalValue); }
            }
        });
    }

    [Test]
    public void InterveningCallRejectsChangedOriginalImplementationAndParameterAbi()
    {
        WithFixture(app =>
        {
            var method = Method(app, "ReadTwiceAfterEffect");
            var native = X86Utils.Iterate(method).ToArray();
            var target = Method(app, "Mark");
            Assert.That(X64GuardedArrayOperationProof.Find(method, native), Is.Not.Null);
            foreach (var candidate in new[] { method, target })
            {
                var injected = new GenericParameterTypeAnalysisContext("T", 0,
                    Il2CppTypeEnum.IL2CPP_TYPE_MVAR, GenericParameterAttributes.None, candidate);
                try
                {
                    Assert.That(candidate.Definition!.GenericContainer, Is.Null);
                    candidate.GenericParameters.Add(injected);
                    Assert.That(X64GuardedArrayOperationProof.Find(method, native), Is.Null,
                        "An injected generic declaration cannot inherit an ordinary native ABI proof.");
                }
                finally { candidate.GenericParameters.Remove(injected); }
            }
            var flags = target.Definition!.iflags;
            try
            {
                foreach (var flag in new[] { MethodImplAttributes.Native, MethodImplAttributes.Runtime,
                             MethodImplAttributes.Unmanaged, MethodImplAttributes.Synchronized })
                {
                    target.Definition.iflags = (ushort)(flags | (ushort)flag);
                    Assert.That(X64GuardedArrayOperationProof.Find(method, native), Is.Null, flag.ToString());
                }
                target.Definition.iflags = (ushort)(flags | 0xF000);
                Assert.That(X64GuardedArrayOperationProof.Find(method, native), Is.Null);
            }
            finally { target.Definition.iflags = flags; }
            Assert.That(X64GuardedArrayOperationProof.Find(method, native), Is.Not.Null);

            var touch = Method(app, "TouchNode");
            var touchNative = X86Utils.Iterate(touch).ToArray();
            var setter = Method(app, "SetValue");
            Assert.That(X64GuardedArrayOperationProof.Find(touch, touchNative), Is.Not.Null);
            try
            {
                setter.Parameters[0].OverrideParameterType = app.SystemTypes.SystemDoubleType;
                Assert.That(X64GuardedArrayOperationProof.Find(touch, touchNative), Is.Null);
            }
            finally { setter.Parameters[0].OverrideParameterType = null; }
        });
    }

    private static MethodAnalysisContext Method(ApplicationAnalysisContext app, string name) =>
        app.GetAssemblyByName("GuardedArrayOperationsFixture")!.Types.SelectMany(type => type.Methods)
            .Single(method => method.Name == name);

    private static void WithFixture(Action<ApplicationAnalysisContext> action)
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_GUARDED_ARRAY_OPERATIONS_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_GUARDED_ARRAY_OPERATIONS_FIXTURE_INPUT to the neutral player-input directory.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
                Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            action(Cpp2IlApi.CurrentAppContext!);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
