using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using AsmResolver.PE.DotNet.Cil;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class IntegerTruncationFixtureTests
{
    [TestCase("LowSigned")]
    [TestCase("LowUnsigned")]
    [TestCase("HighSigned")]
    [TestCase("HighUnsigned")]
    [TestCase("SplitSigned")]
    [TestCase("SplitUnsigned")]
    public void NativeLow32MovementRetainsFull64SourceAndRejectsChangedFinalProvenance(string name)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_INTEGER_TRUNCATION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_INTEGER_TRUNCATION_FIXTURE_INPUT to the neutral exact player input.");
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
            var owner = app.GetAssemblyByName("IntegerTruncationFixture")!.Types.Single(type => type.Name == "IntegerHalves");
            var method = owner.Methods.Single(candidate => candidate.Name == name);
            method.Analyze();
            Assert.That(method.AnalysisWarnings, Is.Empty);
            Assert.That(IntegerTruncationRecovery.HasEvidence(method), Is.True);
            Assert.That(IntegerTruncationRecovery.IsValidFor(method), Is.True);
            var graph = method.ControlFlowGraph!;
            var conversions = graph.Instructions.Where(instruction => instruction.OpCode == OpCode.IntegerExtend).ToArray();
            Assert.That(conversions, Has.Length.EqualTo(name.StartsWith("Split", StringComparison.Ordinal) ? 2 : 1));
            var conversion = conversions[0];
            var result = (LocalVariable)conversion.Operands[0];
            var incoming = method.ParameterLocals.Single(local => !local.IsThis && !local.IsMethodInfo);
            var definition = method.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!;
            var block = graph.FindBlockByInstruction(conversion)!;
            var ret = graph.Instructions.Single(instruction => instruction.OpCode == OpCode.Return);

            void Reject(Action mutate, Action restore)
            {
                try
                {
                    mutate();
                    Assert.That(IntegerTruncationRecovery.IsValidFor(method), Is.False);
                    Assert.That(() => IlGenerator.GenerateIl(method, definition),
                        Throws.TypeOf<DecompilerException>().With.Message.Contains("Native integer truncation proof"));
                }
                finally { restore(); }
                Assert.That(IntegerTruncationRecovery.IsValidFor(method), Is.True);
            }

            Reject(() => method.Attributes |= MethodAttributes.PinvokeImpl, () => method.OverrideAttributes = null);
            Reject(() => method.ImplAttributes |= MethodImplAttributes.InternalCall, () => method.OverrideImplAttributes = null);
            var methodFlags = method.Definition!.flags;
            Reject(() => method.Definition.flags ^= (ushort)MethodAttributes.HideBySig, () => method.Definition.flags = methodFlags);
            var methodToken = method.Definition.token;
            Reject(() => method.Definition.token ^= 1, () => method.Definition.token = methodToken);
            Reject(() => method.OverrideReturnType = method.DefaultReturnType, () => method.OverrideReturnType = null);
            Reject(() => method.Parameters[0].OverrideParameterType = app.SystemTypes.SystemInt32Type,
                () => method.Parameters[0].OverrideParameterType = null);
            Reject(() => conversion.IntegerBitWidth = 32, () => conversion.IntegerBitWidth = 0);
            Reject(() => conversion.SetOperand(2, new Immediate(64)), () => conversion.SetOperand(2, new Immediate(32)));
            var signed = conversion.Operands[4];
            Reject(() => conversion.SetOperand(4, new Immediate(((Immediate)signed).Value ^ 1)), () => conversion.SetOperand(4, signed));
            var resultType = result.Type;
            Reject(() => result.Type = app.SystemTypes.SystemInt64Type, () => result.Type = resultType);
            var resultRegister = result.Register;
            Reject(() => result.Register = new Register(resultRegister.Number + 1, resultRegister.Name, resultRegister.Version),
                () => result.Register = resultRegister);
            Reject(() => result.IsMethodInfo = true, () => result.IsMethodInfo = false);
            Reject(() => incoming.IsMethodInfo = true, () => incoming.IsMethodInfo = false);
            var originalRegister = incoming.Register;
            Reject(() => incoming.Register = originalRegister.Copy(0), () => incoming.Register = originalRegister);
            Reject(() => incoming.Register = new Register(originalRegister.Number + 1, originalRegister.Name),
                () => incoming.Register = originalRegister);
            var parameterIndex = method.ParameterLocals.IndexOf(incoming);
            Reject(() => method.ParameterLocals.Remove(incoming), () => method.ParameterLocals.Insert(parameterIndex, incoming));
            Reject(() => method.ParameterLocals.Add(incoming), () => method.ParameterLocals.RemoveAt(method.ParameterLocals.Count - 1));
            var alias = new LocalVariable("competingIncoming", incoming.Register, incoming.Type);
            Reject(() => method.Locals.Add(alias), () => method.Locals.Remove(alias));
            var source = conversion.Operands[1];
            var forged = new LocalVariable("forgedIncoming", incoming.Register, incoming.Type);
            Reject(() => conversion.SetOperand(1, forged), () => conversion.SetOperand(1, source));
            var abiSlot = method.IsStatic ? 0 : 1;
            var operand = method.ParameterOperands[abiSlot];
            Reject(() => method.ParameterOperands[abiSlot] = new Register(null, "r10"),
                () => method.ParameterOperands[abiSlot] = operand);
            var bytes = method.RawBytes;
            Reject(() =>
            {
                var changed = bytes.AsSpan().ToArray();
                changed[0] ^= 1;
                method.RawBytes = new BinarySlice(changed);
            }, () => method.RawBytes = bytes);
            var interior = method.UnderlyingPointer + 1;
            Reject(() => app.MethodsByAddress.Add(interior, [method]), () => app.MethodsByAddress.Remove(interior));

            var binding = method.GetExtraData<object>("x64-integer-truncation")!;
            Reject(() => method.PutExtraData<object>("x64-integer-truncation", null!),
                () => method.PutExtraData("x64-integer-truncation", binding));
            var earlyReturn = new Instruction(-1, OpCode.Return) { NativeAddress = ret.NativeAddress };
            Reject(() => block.Instructions.Insert(0, earlyReturn), () => block.Instructions.Remove(earlyReturn));
            var unexpected = new Instruction(-1, OpCode.IntegerExtend,
                new LocalVariable("wideObserver", new Register(null, "wide_observer", 0), app.SystemTypes.SystemUInt64Type),
                result, new Immediate(32), new Immediate(64), new Immediate(0)) { NativeAddress = conversion.NativeAddress };
            Reject(() => block.Instructions.Insert(block.Instructions.IndexOf(conversion) + 1, unexpected),
                () => block.Instructions.Remove(unexpected));
            var escape = new Instruction(-1, OpCode.Move,
                new LocalVariable("escapedSource", new Register(null, "escaped_source", 0)), new AddressOf(incoming));
            Reject(() => block.Instructions.Insert(0, escape), () => block.Instructions.Remove(escape));

            var shift = graph.Instructions.SingleOrDefault(instruction => instruction.OpCode is OpCode.ShiftRight or OpCode.ShiftRightUnsigned);
            if (shift != null)
            {
                var count = shift.Operands[2];
                Reject(() => shift.IntegerBitWidth = 32, () => shift.IntegerBitWidth = 64);
                Reject(() => shift.SetOperand(2, new Immediate(31)), () => shift.SetOperand(2, count));
                var opcode = shift.OpCode;
                Reject(() => shift.OpCode = opcode == OpCode.ShiftRight ? OpCode.ShiftRightUnsigned : OpCode.ShiftRight,
                    () => shift.OpCode = opcode);
                var shifted = (LocalVariable)shift.Operands[0];
                var shiftedRegister = shifted.Register;
                Reject(() => shifted.Register = shiftedRegister.Copy(shiftedRegister.Version + 1), () => shifted.Register = shiftedRegister);
                var shiftIndex = block.Instructions.IndexOf(shift);
                var lastConversion = conversions[^1];
                Reject(() =>
                {
                    block.Instructions.Remove(shift);
                    block.Instructions.Insert(block.Instructions.IndexOf(lastConversion) + 1, shift);
                }, () =>
                {
                    block.Instructions.Remove(shift);
                    block.Instructions.Insert(shiftIndex, shift);
                });
                var duplicate = new Instruction(-1, opcode, shifted, incoming, count) { NativeAddress = shift.NativeAddress, IntegerBitWidth = 64 };
                Reject(() => block.Instructions.Insert(shiftIndex, duplicate), () => block.Instructions.Remove(duplicate));
            }

            var stores = graph.Instructions.Where(instruction => instruction.OpCode == OpCode.Move && instruction.Operands[0] is FieldReference).ToArray();
            if (stores.Length != 0)
            {
                Assert.That(stores, Has.Length.EqualTo(2));
                var store = stores[0];
                var access = (FieldReference)store.Operands[0];
                var field = access.Field;
                Reject(() => access.Field = ((FieldReference)stores[1].Operands[0]).Field, () => access.Field = field);
                Reject(() => access.Offset += 4, () => access.Offset = field.Offset);
                Reject(() => field.FieldType = app.SystemTypes.SystemInt64Type, () => field.OverrideFieldType = null);
                Reject(() => field.Attributes |= FieldAttributes.Static, () => field.OverrideAttributes = null);
                var attributes = field.BackingData!.Attributes;
                Reject(() => field.BackingData.Attributes |= FieldAttributes.InitOnly, () => field.BackingData.Attributes = attributes);
                Reject(() => field.BackingData.Attributes = (attributes & ~FieldAttributes.FieldAccessMask) | FieldAttributes.Private,
                    () => field.BackingData.Attributes = attributes);
                var fieldToken = field.BackingData.Field.token;
                Reject(() => field.BackingData.Field.token ^= 1, () => field.BackingData.Field.token = fieldToken);
                var receiver = access.Local;
                Reject(() => access.Local = new LocalVariable("forgedThis", receiver.Register, receiver.Type) { IsThis = true },
                    () => access.Local = receiver);
                Reject(() => store.IntegerBitWidth = 64, () => store.IntegerBitWidth = 32);
                Reject(() => store.SetOperand(1, incoming), () => store.SetOperand(1, result));
                var position = block.Instructions.IndexOf(store);
                Reject(() =>
                {
                    block.Instructions.Remove(store);
                    block.Instructions.Insert(block.Instructions.IndexOf(stores[1]) + 1, store);
                }, () =>
                {
                    block.Instructions.Remove(store);
                    block.Instructions.Insert(position, store);
                });
                Reject(() => block.Instructions.Remove(store), () => block.Instructions.Insert(position, store));
            }
            IlGenerator.GenerateIl(method, definition);
            var expected = name.EndsWith("Unsigned", StringComparison.Ordinal) ? CilOpCodes.Conv_U4 : CilOpCodes.Conv_I4;
            Assert.That(definition.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == expected), Is.EqualTo(conversions.Length));
            Assert.That(definition.CilMethodBody.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Stfld), Is.EqualTo(stores.Length));
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
