using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class GuardedArrayLengthFixtureTests
{
    [TestCase("ReadParameter")]
    [TestCase("ReadObjects")]
    [TestCase("CopyLength")]
    [TestCase("Advance")]
    [TestCase("BeforeLast")]
    public void GuardedLengthPreservesArrayIdentityInt32AndPriorEffects(string name)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_GUARDED_ARRAY_LENGTH_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_GUARDED_ARRAY_LENGTH_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var owner = app.GetAssemblyByName("GuardedArrayLengthFixture")!.Types.Single(type => type.Name == "LengthState");
            var method = owner.Methods.Single(candidate => candidate.Name == name);
            method.Analyze();
            Assert.That(method.AnalysisWarnings, Is.Empty);
            Assert.That(ArrayLengthReadRecovery.GetEvidence(method), Has.Count.EqualTo(1));
            Assert.That(ArrayLengthReadRecovery.IsValidFor(method), Is.True);
            var evidence = ArrayLengthReadRecovery.GetEvidence(method).Single();
            var definition = method.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!;
            var graph = method.ControlFlowGraph!;
            Assert.Multiple(() =>
            {
                Assert.That(evidence.GuardRecorded, Is.True);
                Assert.That(evidence.Native.ComparisonCapture, Is.EqualTo(name == "Advance"));
                Assert.That(evidence.RequiresNullProbe, Is.EqualTo(name == "BeforeLast"));
                Assert.That(evidence.Result.Type, Is.SameAs(app.SystemTypes.SystemInt32Type));
                Assert.That(evidence.Operation.IntegerBitWidth, Is.EqualTo(32));
                Assert.That(graph.Instructions.Any(instruction => instruction.OpCode == OpCode.RuntimeNullThrow), Is.False);
            });

            void Reject(Action mutate, Action restore)
            {
                try
                {
                    mutate();
                    Assert.That(ArrayLengthReadRecovery.IsValidFor(method), Is.False);
                    Assert.That(() => IlGenerator.GenerateIl(method, definition),
                        Throws.TypeOf<DecompilerException>().With.Message.Contains("Guarded array Length proof"));
                }
                finally { restore(); }
                Assert.That(ArrayLengthReadRecovery.IsValidFor(method), Is.True);
            }

            Reject(() => method.Attributes |= MethodAttributes.PinvokeImpl, () => method.OverrideAttributes = null);
            Reject(() => method.ImplAttributes |= MethodImplAttributes.InternalCall, () => method.OverrideImplAttributes = null);
            Reject(() => method.Name = "other_length", () => method.OverrideName = null);
            Reject(() => method.OverrideReturnType = method.DefaultReturnType, () => method.OverrideReturnType = null);
            Reject(() => evidence.Operation.IntegerBitWidth = 64, () => evidence.Operation.IntegerBitWidth = 32);
            Reject(() => evidence.Operation.IntegerBitWidth = 0, () => evidence.Operation.IntegerBitWidth = 32);
            Reject(() => evidence.Result.Type = app.SystemTypes.SystemUInt32Type,
                () => evidence.Result.Type = app.SystemTypes.SystemInt32Type);
            var resultRegister = evidence.Result.Register;
            Reject(() => evidence.Result.Register = new Register(resultRegister.Number + 1, resultRegister.Name, resultRegister.Version),
                () => evidence.Result.Register = resultRegister);
            Reject(() => evidence.Access.Array = new LocalVariable("other_array", evidence.Array.Register, evidence.Array.Type),
                () => evidence.Access.Array = evidence.Array);
            Reject(() => evidence.Operation.SetOperand(1, new ArrayLength(evidence.Array)),
                () => evidence.Operation.SetOperand(1, evidence.Access));
            Reject(() => evidence.Array.IsMethodInfo = true, () => evidence.Array.IsMethodInfo = false);
            var bytes = method.RawBytes;
            Reject(() =>
            {
                var changed = bytes.AsSpan().ToArray();
                changed[0] ^= 1;
                method.RawBytes = new BinarySlice(changed);
            }, () => method.RawBytes = bytes);

            if (name == "ReadParameter")
            {
                var step = evidence.PrefixEffects.Select(effect => effect.Operation).Single(operation =>
                    operation.OpCode == OpCode.Add && operation.Operands[0] is FieldReference);
                var stepped = (FieldReference)step.Operands[0];
                var fieldDefinition = stepped.Field.BackingData!.Field;
                var originalTypeIndex = fieldDefinition.typeIndex;
                var native = X64NativeInstructionReader.ReadRootBody(method)!;
                Assert.That(ArrayLengthReadRecovery.HasBoundPrefixProjection(method, native, step), Is.True);
                var unsignedTypeIndex = app.SystemTypes.SystemUInt32Type.Fields.First(field =>
                    field.BackingData?.Field.RawFieldType?.Type == Il2CppTypeEnum.IL2CPP_TYPE_U4).BackingData!.Field.typeIndex;
                try
                {
                    fieldDefinition.typeIndex = unsignedTypeIndex;
                    Assert.That(stepped.Field.OverrideFieldType, Is.Null);
                    Assert.That(ArrayLengthReadRecovery.HasBoundPrefixProjection(method, native, step), Is.True);
                }
                finally { fieldDefinition.typeIndex = originalTypeIndex; }
                var singleTypeIndex = app.SystemTypes.SystemSingleType.Fields.First(field =>
                    field.BackingData?.Field.RawFieldType?.Type == Il2CppTypeEnum.IL2CPP_TYPE_R4).BackingData!.Field.typeIndex;
                Reject(() =>
                {
                    // Change the original type projection, without an override:
                    // a valid Single layout still cannot turn INC bits into +1f.
                    fieldDefinition.typeIndex = singleTypeIndex;
                    Assert.That(stepped.Field.OverrideFieldType, Is.Null);
                    Assert.That(NarrowFieldEqualityProof.HasUnchangedFloatingFieldLayout(stepped, 32), Is.True);
                    Assert.That(ArrayLengthReadRecovery.HasBoundPrefixProjection(method, native, step), Is.False);
                }, () => fieldDefinition.typeIndex = originalTypeIndex);
            }

            var block = graph.FindBlockByInstruction(evidence.Operation)!;
            var oldPosition = block.Instructions.IndexOf(evidence.Operation);
            var prior = graph.Blocks.SelectMany(current => current.Instructions).Last(instruction =>
                instruction.NativeAddress < evidence.Operation.NativeAddress &&
                instruction.Operands.Any(operand => operand is FieldReference));
            var priorBlock = graph.FindBlockByInstruction(prior)!;
            Reject(() =>
            {
                block.Instructions.Remove(evidence.Operation);
                priorBlock.Instructions.Insert(priorBlock.Instructions.IndexOf(prior), evidence.Operation);
            }, () =>
            {
                priorBlock.Instructions.Remove(evidence.Operation);
                block.Instructions.Insert(oldPosition, evidence.Operation);
            });

            if (evidence.Capture is { } capture && evidence.Source is { } source)
            {
                var originalRegister = evidence.Array.Register;
                Reject(() => evidence.Array.Register = new Register(originalRegister.Number + 1, originalRegister.Name, originalRegister.Version),
                    () => evidence.Array.Register = originalRegister);
                Reject(() => source.Offset++, () => source.Offset = source.Field.Offset);
                Reject(() => source.Field.FieldType = app.SystemTypes.SystemObjectType, () => source.Field.OverrideFieldType = null);
                var ownerLocal = source.Local;
                var ownerRegister = ownerLocal.Register;
                Reject(() => ownerLocal.Register = ownerRegister.Copy(0), () => ownerLocal.Register = ownerRegister);
                var pe = (PE)app.Binary;
                var buffer = ((MemoryStream)pe.BaseStream).GetBuffer();
                var pointer = app.Binary.TypeDefinitionSizePointers[owner.Definition!.TypeIndex!.Value];
                var raw = checked((int)pe.MapVirtualAddressToRaw(pointer));
                var oldSize = buffer.AsSpan(raw, 4).ToArray();
                Reject(() => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(raw, 4), (uint)(source.Offset + 7)),
                    () => oldSize.CopyTo(buffer, raw));
                var oldCapture = capture.Operands[1];
                Reject(() => capture.SetOperand(1, new LocalVariable("invented_origin", evidence.Array.Register, evidence.Array.Type)),
                    () => capture.SetOperand(1, oldCapture));
            }
            else
            {
                var parameter = method.Parameters.Single();
                var originalRegister = evidence.Array.Register;
                Reject(() => evidence.Array.Register = originalRegister.Copy(0), () => evidence.Array.Register = originalRegister);
                Reject(() => parameter.OverrideParameterType = app.SystemTypes.SystemObjectType,
                    () => parameter.OverrideParameterType = null);
                var raw = parameter.Definition!.RawType!;
                Reject(() => raw.Pinned = 1, () => raw.Pinned = 0);
            }
            if (evidence.RequiresNullProbe)
            {
                var probe = graph.Instructions.Single(instruction => !ReferenceEquals(instruction, evidence.Operation) &&
                    instruction.Operands is [LocalVariable, ArrayLength]);
                var probeBlock = graph.FindBlockByInstruction(probe)!;
                var probeValue = (LocalVariable)probe.Operands[0];
                var probeAccess = (ArrayLength)probe.Operands[1];
                var guard = probeBlock.Predecessors.Single();
                var branch = guard.Instructions.Last();
                var condition = (LocalVariable)branch.Operands[1];
                var comparison = graph.Instructions.Single(instruction => ReferenceEquals(instruction.Destination, condition));
                Assert.That(ArrayLengthReadRecovery.HasRecordedNullProbeComparison(method, comparison, evidence.Array), Is.True);
                var otherComparison = new Instruction(-1, OpCode.CheckEqual, condition, evidence.Array, new Immediate(0))
                    { IntegerBitWidth = 64, NativeAddress = comparison.NativeAddress };
                Assert.That(ArrayLengthReadRecovery.HasRecordedNullProbeComparison(method, otherComparison, evidence.Array), Is.False);
                Assert.That(ArrayLengthReadRecovery.HasRecordedNullProbeComparison(method, comparison,
                    new LocalVariable("other_comparison_array", evidence.Array.Register, evidence.Array.Type)), Is.False);
                Reject(() => probe.IntegerBitWidth = 64, () => probe.IntegerBitWidth = 32);
                Reject(() => probeValue.Type = app.SystemTypes.SystemInt64Type,
                    () => probeValue.Type = app.SystemTypes.SystemInt32Type);
                Reject(() => probeAccess.Array = new LocalVariable("other_probe_array", evidence.Array.Register, evidence.Array.Type),
                    () => probeAccess.Array = evidence.Array);
                var probeRegister = probeValue.Register;
                Reject(() => probeValue.Register = probeRegister.Copy(probeRegister.Version + 1),
                    () => probeValue.Register = probeRegister);
                Reject(() => branch.SetOperand(0, probeBlock.Successors.Single()), () => branch.SetOperand(0, probeBlock));
                Reject(() => comparison.SetOperand(2, new Immediate(1)), () => comparison.SetOperand(2, new Immediate(0)));
                var originalProbeAddress = probe.NativeAddress;
                Reject(() => probe.NativeAddress = evidence.Native.ReadAddress, () => probe.NativeAddress = originalProbeAddress);
                var setup = evidence.SetupReads.Single();
                Reject(() => setup.Source.Offset++, () => setup.Source.Offset = setup.Source.Field.Offset);
                Reject(() => setup.Source.Field.FieldType = app.SystemTypes.SystemUInt32Type,
                    () => setup.Source.Field.OverrideFieldType = null);
                var setupBlock = graph.FindBlockByInstruction(setup.Operation)!;
                var setupPosition = setupBlock.Instructions.IndexOf(setup.Operation);
                Reject(() =>
                {
                    setupBlock.Instructions.Remove(setup.Operation);
                    setupBlock.Instructions.Insert(setupBlock.Instructions.IndexOf(evidence.Operation) + 1, setup.Operation);
                }, () =>
                {
                    setupBlock.Instructions.Remove(setup.Operation);
                    setupBlock.Instructions.Insert(setupPosition, setup.Operation);
                });
                var duplicateCondition = new Instruction(-1, OpCode.Move, condition, new Immediate(1));
                Reject(() => guard.Instructions.Insert(guard.Instructions.IndexOf(branch), duplicateCondition),
                    () => guard.Instructions.Remove(duplicateCondition));
                var escapedCondition = new Instruction(-1, OpCode.Move,
                    new LocalVariable("condition_address", new Register(null, "condition_address")), new AddressOf(condition));
                Reject(() => guard.Instructions.Insert(guard.Instructions.IndexOf(branch), escapedCondition),
                    () => guard.Instructions.Remove(escapedCondition));
                var retainedCapture = evidence.Capture!;
                var captureBlock = graph.FindBlockByInstruction(retainedCapture)!;
                var capturePosition = captureBlock.Instructions.IndexOf(retainedCapture);
                Reject(() =>
                {
                    captureBlock.Instructions.Remove(retainedCapture);
                    guard.Instructions.Insert(guard.Instructions.IndexOf(comparison) + 1, retainedCapture);
                }, () =>
                {
                    guard.Instructions.Remove(retainedCapture);
                    captureBlock.Instructions.Insert(capturePosition, retainedCapture);
                });
                Reject(() =>
                {
                    captureBlock.Instructions.Remove(retainedCapture);
                    setupBlock.Instructions.Insert(setupBlock.Instructions.IndexOf(setup.Operation), retainedCapture);
                }, () =>
                {
                    setupBlock.Instructions.Remove(retainedCapture);
                    captureBlock.Instructions.Insert(capturePosition, retainedCapture);
                });
                // Move only the pure comparison ahead of its producer, keeping
                // every observable native-origin read/write in the same order.
                var comparisonPosition = guard.Instructions.IndexOf(comparison);
                Reject(() =>
                {
                    guard.Instructions.Remove(comparison);
                    captureBlock.Instructions.Insert(captureBlock.Instructions.IndexOf(retainedCapture), comparison);
                }, () =>
                {
                    captureBlock.Instructions.Remove(comparison);
                    guard.Instructions.Insert(comparisonPosition, comparison);
                });
                var increment = evidence.PrefixEffects.Select(effect => effect.Operation).Single(operation =>
                    operation.OpCode == OpCode.Add && operation.Operands[0] is FieldReference);
                var incrementBlock = graph.FindBlockByInstruction(increment)!;
                var incrementPosition = incrementBlock.Instructions.IndexOf(increment);
                Reject(() =>
                {
                    incrementBlock.Instructions.Remove(increment);
                    setupBlock.Instructions.Insert(setupBlock.Instructions.IndexOf(setup.Operation), increment);
                }, () =>
                {
                    setupBlock.Instructions.Remove(increment);
                    incrementBlock.Instructions.Insert(incrementPosition, increment);
                });
                Reject(() => incrementBlock.Instructions.Remove(increment),
                    () => incrementBlock.Instructions.Insert(incrementPosition, increment));
                var oldDelta = increment.Operands[2];
                Reject(() => increment.SetOperand(2, new Immediate(2)), () => increment.SetOperand(2, oldDelta));
                var written = (FieldReference)increment.Operands[0];
                var writtenField = written.Field;
                var writtenReceiver = written.Local;
                var writtenOffset = written.Offset;
                // The operand object is deliberately retained: a shallow saved
                // operand list cannot detect these changed field projections.
                Reject(() => written.Field = owner.Fields.Single(field => field.Name == "Marker"),
                    () => written.Field = writtenField);
                Reject(() => written.Local = new LocalVariable("other_owner", writtenReceiver.Register, writtenReceiver.Type)
                    { IsThis = true }, () => written.Local = writtenReceiver);
                Reject(() => written.Offset++, () => written.Offset = writtenOffset);
                Reject(() => writtenField.Attributes |= FieldAttributes.InitOnly,
                    () => writtenField.OverrideAttributes = null);
            }
            Assert.That(() => IlGenerator.GenerateIl(method, definition), Throws.Nothing);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
