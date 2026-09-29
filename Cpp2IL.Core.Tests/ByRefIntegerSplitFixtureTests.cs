using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class ByRefIntegerSplitFixtureTests
{
    [Test]
    public void ExactSplitsRetainOriginalArgumentsWidthsAndOrderedAliasingWrites()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_BYREF_INTEGER_HALVES_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_BYREF_INTEGER_HALVES_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var methods = app.GetAssemblyByName("ByRefIntegerHalvesFixture")!.Types
                .Where(type => type.Name is "Splitters" or "SplitState").SelectMany(type => type.Methods)
                .Where(method => method.Name.StartsWith("Split", StringComparison.Ordinal)).ToArray();
            Assert.That(methods, Has.Length.EqualTo(6), "Each distinct static/instance declaration is checked, including folded native entries.");
            foreach (var method in methods)
            {
                var label = method.FullName;
                var proof = X64ByRefIntegerSplitProof.Find(method);
                Assert.That(proof, Is.Not.Null, label);
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty, label);
                Assert.That(ByRefIntegerSplitRecovery.HasEvidence(method), Is.True, label);
                Assert.That(ByRefIntegerSplitRecovery.IsValidFor(method), Is.True, label);
                var graph = method.ControlFlowGraph!;
                var block = graph.Blocks.Single(candidate => candidate != graph.EntryBlock && candidate != graph.ExitBlock);
                var operations = block.Instructions.Where(instruction => instruction.OpCode != OpCode.Nop).ToArray();
                Assert.That(operations, Has.Length.EqualTo(6), label);
                var lowConversion = operations[0];
                var lowStore = operations[1];
                var shift = operations[2];
                var highConversion = operations[3];
                var highStore = operations[4];
                var ret = operations[5];
                var source = (LocalVariable)lowConversion.Operands[1];
                var low = (LocalVariable)lowConversion.Operands[0];
                var shifted = (LocalVariable)shift.Operands[0];
                var high = (LocalVariable)highConversion.Operands[0];
                var lowMemory = (MemoryOperand)lowStore.Operands[0];
                var highMemory = (MemoryOperand)highStore.Operands[0];
                var lowPointer = (LocalVariable)lowMemory.Base!;
                var highPointer = (LocalVariable)highMemory.Base!;
                Assert.That(IntegerExtension.StorageBits(source.Type, app.SystemTypes), Is.EqualTo(64), label);
                Assert.That(IntegerExtension.StorageBits(shifted.Type, app.SystemTypes), Is.EqualTo(64), label);
                Assert.That(low.Type, Is.SameAs(((ByRefTypeAnalysisContext)lowPointer.Type!).ElementType), label);
                Assert.That(high.Type, Is.SameAs(((ByRefTypeAnalysisContext)highPointer.Type!).ElementType), label);
                var definition = method.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!;

                void Reject(string change, Action mutate, Action restore)
                {
                    try
                    {
                        mutate();
                        Assert.That(ByRefIntegerSplitRecovery.IsValidFor(method), Is.False, label + ": " + change);
                        Assert.That(() => IlGenerator.GenerateIl(method, definition),
                            Throws.TypeOf<DecompilerException>().With.Message.Contains("Byref integer split proof"), label + ": " + change);
                    }
                    finally { restore(); }
                    Assert.That(ByRefIntegerSplitRecovery.IsValidFor(method), Is.True, label + ": restored " + change);
                }

                Reject("store width", () => lowStore.IntegerBitWidth = 64, () => lowStore.IntegerBitWidth = 32);
                Reject("shift width", () => shift.IntegerBitWidth = 32, () => shift.IntegerBitWidth = 64);
                var shiftCode = shift.OpCode;
                Reject("shift signedness", () => shift.OpCode = shiftCode == OpCode.ShiftRight ? OpCode.ShiftRightUnsigned : OpCode.ShiftRight,
                    () => shift.OpCode = shiftCode);
                Reject("shift count", () => shift.SetOperand(2, new Immediate(31)), () => shift.SetOperand(2, new Immediate(32)));
                Reject("shift producer", () => highConversion.SetOperand(1, source), () => highConversion.SetOperand(1, shifted));
                Reject("low capture source", () => lowConversion.SetOperand(1, shifted), () => lowConversion.SetOperand(1, source));
                Reject("store capture", () => highStore.SetOperand(1, low), () => highStore.SetOperand(1, high));
                Reject("pointer identity", () => lowStore.SetOperand(0, highMemory), () => lowStore.SetOperand(0, lowMemory));
                Reject("partial referent offset", () => lowStore.SetOperand(0, lowMemory with { Addend = 4 }), () => lowStore.SetOperand(0, lowMemory));
                Reject("indexed referent", () => lowStore.SetOperand(0, lowMemory with { Index = source }), () => lowStore.SetOperand(0, lowMemory));
                Reject("forged pointer", () => lowStore.SetOperand(0, lowMemory with { Base = new LocalVariable("copy", lowPointer.Register, lowPointer.Type) }),
                    () => lowStore.SetOperand(0, lowMemory));
                var originalType = source.Type;
                Reject("source narrowed", () => source.Type = app.SystemTypes.SystemUInt32Type, () => source.Type = originalType);
                var shiftedType = shifted.Type;
                Reject("shift narrowed", () => shifted.Type = app.SystemTypes.SystemInt32Type, () => shifted.Type = shiftedType);
                var pointerType = lowPointer.Type;
                Reject("referent widened", () => lowPointer.Type = new ByRefTypeAnalysisContext(app.SystemTypes.SystemInt64Type), () => lowPointer.Type = pointerType);
                foreach (var incoming in new[] { source, lowPointer, highPointer })
                {
                    var register = incoming.Register;
                    Reject("incoming SSA version", () => incoming.Register = register.Copy(0), () => incoming.Register = register);
                    Reject("incoming register slot", () => incoming.Register = new Register(register.Number + 1, register.Name), () => incoming.Register = register);
                    var position = method.ParameterLocals.IndexOf(incoming);
                    Reject("removed original argument", () => method.ParameterLocals.Remove(incoming), () => method.ParameterLocals.Insert(position, incoming));
                    Reject("duplicate argument", () => method.ParameterLocals.Add(incoming), () => method.ParameterLocals.RemoveAt(method.ParameterLocals.Count - 1));
                    var forged = new LocalVariable("forged", register, incoming.Type);
                    Reject("competing slot", () => method.Locals.Add(forged), () => method.Locals.Remove(forged));
                }
                foreach (var result in new[] { low, shifted, high })
                {
                    var register = result.Register;
                    Reject("private producer version", () => result.Register = register.Copy(register.Version + 1), () => result.Register = register);
                }
                Reject("write order", () => { block.Instructions.Remove(lowStore); block.Instructions.Insert(block.Instructions.IndexOf(highStore) + 1, lowStore); },
                    () => { block.Instructions.Remove(lowStore); block.Instructions.Insert(block.Instructions.IndexOf(lowConversion) + 1, lowStore); });
                Reject("missing high write", () => block.Instructions.Remove(highStore), () => block.Instructions.Insert(block.Instructions.IndexOf(ret), highStore));
                var extra = new Instruction(-1, OpCode.Move, highMemory, low) { IntegerBitWidth = 32 };
                Reject("additional write", () => block.Instructions.Insert(1, extra), () => block.Instructions.Remove(extra));
                var escape = new Instruction(-1, OpCode.Move, shifted, new AddressOf(source));
                Reject("addressable value", () => block.Instructions.Insert(1, escape), () => block.Instructions.Remove(escape));
                var detached = new Block { Instructions = [extra] };
                Reject("detached effects", () => graph.Blocks.Add(detached), () => graph.Blocks.Remove(detached));
                var methodInfoSlot = method.ParameterOperands[^1];
                Reject("MethodInfo slot alias", () => method.ParameterOperands[^1] = source.Register,
                    () => method.ParameterOperands[^1] = methodInfoSlot);
                if (!method.IsStatic)
                {
                    var methodInfoOffset = (StackOffset)new X64CallingConventionResolver().ResolveForParameters(method)[^1];
                    Reject("MethodInfo offset", () => method.ParameterOperands[^1] = new Register(null,
                        StackAnalyzer.NameForSlot(new StackOffset(methodInfoOffset.Offset + 8))),
                        () => method.ParameterOperands[^1] = methodInfoSlot);
                }
                var address = lowStore.NativeAddress;
                Reject("native site", () => lowStore.NativeAddress++, () => lowStore.NativeAddress = address);
                var direction = method.Parameters[1].Attributes;
                Reject("readonly direction", () => method.Parameters[1].OverrideAttributes = ParameterAttributes.In, () => method.Parameters[1].OverrideAttributes = null);
                var rawParameterType = method.Parameters[1].Definition!.RawType!;
                var rawAttributes = rawParameterType.Attrs;
                Reject("raw direction drift", () => rawParameterType.Attrs = (uint)(direction == ParameterAttributes.Out ? ParameterAttributes.None : ParameterAttributes.Out),
                    () => rawParameterType.Attrs = rawAttributes);
                var rawModifiers = rawParameterType.NumMods;
                Reject("unknown parameter modifiers", () => rawParameterType.NumMods = 1,
                    () => rawParameterType.NumMods = rawModifiers);
                var rawDefinition = method.Definition!;
                var token = rawDefinition.token;
                Reject("raw method identity", () => rawDefinition.token ^= 1, () => rawDefinition.token = token);
                var flags = rawDefinition.flags;
                Reject("raw method flags", () => rawDefinition.flags ^= (ushort)MethodAttributes.HideBySig, () => rawDefinition.flags = flags);
                Reject("managed implementation", () => method.OverrideImplAttributes = MethodImplAttributes.InternalCall, () => method.OverrideImplAttributes = null);
                var bytes = method.RawBytes;
                Reject("cached native bytes", () => { var changed = bytes.AsSpan().ToArray(); changed[0] ^= 1; method.RawBytes = new BinarySlice(changed); },
                    () => method.RawBytes = bytes);
                var interior = method.UnderlyingPointer + 1;
                Reject("interior managed entry", () => app.MethodsByAddress.Add(interior, [method]), () => app.MethodsByAddress.Remove(interior));
                var binding = method.GetExtraData<object>("ByRefIntegerSplitRecovery")!;
                var evidence = X64ByRefIntegerSplitProof.GetEvidence(method)!;
                var registers = new[] { low.Register, shifted.Register, high.Register };
                Reject("lost evidence and renamed captures", () =>
                {
                    method.PutExtraData<object>("ByRefIntegerSplitRecovery", null!);
                    method.PutExtraData<X64ByRefIntegerSplitProof.Proof>(X64ByRefIntegerSplitProof.EvidenceKey, null!);
                    low.Register = new Register(null, "ordinary_low", registers[0].Version);
                    shifted.Register = new Register(null, "ordinary_shift", registers[1].Version);
                    high.Register = new Register(null, "ordinary_high", registers[2].Version);
                    Assert.That(ByRefIntegerSplitRecovery.HasEvidence(method), Is.True, label);
                }, () =>
                {
                    low.Register = registers[0]; shifted.Register = registers[1]; high.Register = registers[2];
                    method.PutExtraData("ByRefIntegerSplitRecovery", binding);
                    method.PutExtraData(X64ByRefIntegerSplitProof.EvidenceKey, evidence);
                });
                IlGenerator.GenerateIl(method, definition);
                Assert.That(definition.CilMethodBody!.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Stobj), Is.EqualTo(2), label);
                Assert.That(definition.CilMethodBody.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Conv_I4 || instruction.OpCode == CilOpCodes.Conv_U4),
                    Is.EqualTo(2), label);
                Assert.That(definition.CilMethodBody.Instructions.Count(instruction => instruction.OpCode == CilOpCodes.Shr || instruction.OpCode == CilOpCodes.Shr_Un),
                    Is.EqualTo(1), label);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
