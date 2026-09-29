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
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class EnumIntegerConversionFixtureTests
{
    [Test]
    public void ExactConversionsRetainEnumIdentityNativeExtensionAndExplicitReturnSign()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ENUM_INTEGER_CONVERSION_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ENUM_INTEGER_CONVERSION_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var methods = app.GetAssemblyByName("EnumIntegerConversionFixture")!.Types
                .Single(type => type.Name == "Conversions").Methods.ToArray();
            Assert.That(methods, Has.Length.EqualTo(8), "Every enum/return identity is checked, including folded native entries.");
            foreach (var method in methods)
            {
                var label = method.FullName;
                var proof = X64EnumIntegerConversionProof.Find(method);
                Assert.That(proof, Is.Not.Null, label);
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty, label);
                Assert.That(EnumIntegerConversionRecovery.HasEvidence(method), Is.True, label);
                Assert.That(EnumIntegerConversionRecovery.IsValidFor(method), Is.True, label);
                var original = proof!;
                var graph = method.ControlFlowGraph!;
                var block = graph.Blocks.Single(candidate => candidate != graph.EntryBlock && candidate != graph.ExitBlock);
                var operations = block.Instructions.Where(operation => operation.OpCode != OpCode.Nop).ToArray();
                Assert.That(operations, Has.Length.EqualTo(original.ConvertSign ? 3 : 2), label);
                var conversion = operations[0];
                var source = (LocalVariable)conversion.Operands[1];
                var capture = (LocalVariable)conversion.Operands[0];
                var ret = operations[^1];
                var returned = (LocalVariable)ret.Operands[0];
                Assert.That(source.Type, Is.SameAs(method.Parameters[0].ParameterType), label);
                Assert.That(source.Type, Is.SameAs(original.Storage.Enum), label);
                Assert.That(source.Type!.IsEnumType, Is.True, label);
                Assert.That(IntegerExtension.StorageBits(source.Type, app.SystemTypes), Is.Zero, label);
                Assert.That(IntegerExtension.TryGet(conversion, out var extension), Is.True, label);
                Assert.That(extension.HasCanonicalTypes(conversion, app), Is.True, label);
                var definition = method.GetExtraData<AsmResolver.DotNet.MethodDefinition>("AsmResolverMethod")!;
                var mutations = 0;

                void Reject(string change, Action mutate, Action restore)
                {
                    try
                    {
                        mutate();
                        Assert.That(EnumIntegerConversionRecovery.IsValidFor(method), Is.False, label + ": " + change);
                        Assert.That(() => IlGenerator.GenerateIl(method, definition),
                            Throws.TypeOf<DecompilerException>().With.Message.Contains("Enum integer conversion proof"), label + ": " + change);
                        mutations++;
                    }
                    finally { restore(); }
                    Assert.That(EnumIntegerConversionRecovery.IsValidFor(method), Is.True, label + ": restored " + change);
                }

                var width = conversion.Operands[2];
                var signed = conversion.Operands[4];
                Reject("source width", () => conversion.SetOperand(2, new Immediate(original.Native.Width == 8 ? 16 : 8)),
                    () => conversion.SetOperand(2, width));
                Reject("result width", () => conversion.SetOperand(3, new Immediate(64)), () => conversion.SetOperand(3, new Immediate(32)));
                Reject("native sign", () => conversion.SetOperand(4, new Immediate(original.Native.Signed ? 0 : 1)),
                    () => conversion.SetOperand(4, signed));
                Reject("implicit integer width", () => conversion.IntegerBitWidth = 32, () => conversion.IntegerBitWidth = 0);
                var address = conversion.NativeAddress;
                Reject("native site", () => conversion.NativeAddress++, () => conversion.NativeAddress = address);
                var sourceType = source.Type;
                Reject("enum erased to backing type", () => source.Type = original.Storage.Underlying, () => source.Type = sourceType);
                Reject("enum erased to result type", () => source.Type = capture.Type, () => source.Type = sourceType);
                var competingEnum = app.GetAssemblyByName("EnumIntegerConversionFixture")!.Types
                    .First(type => type.IsEnumType && !ReferenceEquals(type, original.Storage.Enum));
                Reject("different enum identity", () => source.Type = competingEnum, () => source.Type = sourceType);
                var sourceRegister = source.Register;
                Reject("incoming SSA version", () => source.Register = sourceRegister.Copy(0), () => source.Register = sourceRegister);
                Reject("incoming slot", () => source.Register = new Register(sourceRegister.Number + 1, sourceRegister.Name),
                    () => source.Register = sourceRegister);
                var parameterPosition = method.ParameterLocals.IndexOf(source);
                Reject("missing original parameter", () => method.ParameterLocals.Remove(source),
                    () => method.ParameterLocals.Insert(parameterPosition, source));
                Reject("duplicate original parameter", () => method.ParameterLocals.Add(source),
                    () => method.ParameterLocals.RemoveAt(method.ParameterLocals.Count - 1));
                var forged = new LocalVariable("forged", sourceRegister, source.Type);
                Reject("competing argument slot", () => method.Locals.Add(forged), () => method.Locals.Remove(forged));
                Reject("forged argument read", () => conversion.SetOperand(1, forged), () => conversion.SetOperand(1, source));
                var infoSlot = method.ParameterOperands[^1];
                Reject("MethodInfo slot alias", () => method.ParameterOperands[^1] = source.Register,
                    () => method.ParameterOperands[^1] = infoSlot);
                var captureType = capture.Type;
                Reject("native result widened", () => capture.Type = app.SystemTypes.SystemInt64Type, () => capture.Type = captureType);
                var captureRegister = capture.Register;
                Reject("private SSA version", () => capture.Register = captureRegister.Copy(captureRegister.Version + 1),
                    () => capture.Register = captureRegister);
                var escape = new Instruction(-1, OpCode.Move, capture, new AddressOf(source));
                Reject("addressable enum", () => block.Instructions.Insert(1, escape), () => block.Instructions.Remove(escape));
                var duplicate = new Instruction(-1, OpCode.IntegerExtend, capture, source, width, new Immediate(32), signed);
                Reject("duplicate producer", () => block.Instructions.Insert(1, duplicate), () => block.Instructions.Remove(duplicate));
                Reject("detached emitted operation", () => graph.Blocks.Add(new Block { Instructions = [duplicate] }),
                    () => graph.Blocks.RemoveAt(graph.Blocks.Count - 1));
                Reject("reordered conversion", () => { block.Instructions.Remove(conversion); block.Instructions.Add(conversion); },
                    () => { block.Instructions.Remove(conversion); block.Instructions.Insert(0, conversion); });
                var returnAddress = ret.NativeAddress;
                Reject("return site", () => ret.NativeAddress++, () => ret.NativeAddress = returnAddress);
                if (original.ConvertSign)
                {
                    var returnConversion = operations[1];
                    var returnSign = returnConversion.Operands[4];
                    Reject("explicit return sign", () => returnConversion.SetOperand(4, new Immediate(original.ReturnSigned ? 0 : 1)),
                        () => returnConversion.SetOperand(4, returnSign));
                    Reject("return conversion removed", () => block.Instructions.Remove(returnConversion),
                        () => block.Instructions.Insert(block.Instructions.IndexOf(ret), returnConversion));
                    Reject("return conversion bypassed", () => ret.SetOperand(0, capture), () => ret.SetOperand(0, returned));
                }

                var enumType = original.Storage.Enum;
                var enumDefinition = enumType.Definition!;
                var backing = original.Storage.Backing;
                Reject("enum underlying override", () => enumType.OverrideEnumUnderlyingType = app.SystemTypes.SystemInt32Type,
                    () => enumType.OverrideEnumUnderlyingType = null);
                Reject("backing field override", () => backing.OverrideFieldType = app.SystemTypes.SystemInt32Type,
                    () => backing.OverrideFieldType = null);
                Reject("backing offset", () => backing.OverrideOffset = 1, () => backing.OverrideOffset = null);
                var rawUnderlying = enumDefinition.EnumUnderlyingType!;
                var rawKind = rawUnderlying.Type;
                Reject("unsupported raw underlying cycle", () => rawUnderlying.Type = Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                    () => rawUnderlying.Type = rawKind);
                var modifiers = rawUnderlying.NumMods;
                Reject("underlying modifiers", () => rawUnderlying.NumMods = 1, () => rawUnderlying.NumMods = modifiers);
                var byref = rawUnderlying.Byref;
                Reject("underlying byref", () => rawUnderlying.Byref = 1, () => rawUnderlying.Byref = byref);
                var rawBacking = backing.BackingData!.Field.RawFieldType!;
                var backingKind = rawBacking.Type;
                Reject("unsupported backing descriptor", () => rawBacking.Type = Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                    () => rawBacking.Type = backingKind);
                var rawBase = enumDefinition.RawBaseType!;
                var baseKind = rawBase.Type;
                Reject("unsupported enum base descriptor", () => rawBase.Type = Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY,
                    () => rawBase.Type = baseKind);
                var baseData = rawBase.Data.Dummy;
                Reject("raw enum base provenance", () => rawBase.Data.Dummy ^= 1, () => rawBase.Data.Dummy = baseData);
                var literal = enumType.Fields.First(field => field.IsStatic);
                Reject("enum constant override", () => { literal.UseOverrideConstantValue = true; literal.OverrideConstantValue = 73; },
                    () => { literal.UseOverrideConstantValue = false; literal.OverrideConstantValue = null; });
                var literalAttributes = literal.BackingData!.Attributes;
                Reject("raw enum literal attributes", () => literal.BackingData.Attributes ^= FieldAttributes.InitOnly,
                    () => literal.BackingData.Attributes = literalAttributes);
                var enumNameIndex = enumDefinition.NameIndex;
                Reject("raw enum name index", () => enumDefinition.NameIndex++, () => enumDefinition.NameIndex = enumNameIndex);
                var rawParameter = method.Parameters[0].Definition!.RawType!;
                var parameterModifiers = rawParameter.NumMods;
                Reject("parameter modifiers", () => rawParameter.NumMods = 1, () => rawParameter.NumMods = parameterModifiers);
                var parameterByref = rawParameter.Byref;
                Reject("parameter byref", () => rawParameter.Byref = 1, () => rawParameter.Byref = parameterByref);
                var rawMethod = method.Definition!;
                var flags = rawMethod.flags;
                Reject("raw method declaration flags", () => rawMethod.flags ^= (ushort)MethodAttributes.HideBySig,
                    () => rawMethod.flags = flags);
                var token = rawMethod.token;
                Reject("raw method token", () => rawMethod.token ^= 1, () => rawMethod.token = token);
                Reject("native implementation override", () => method.OverrideImplAttributes = MethodImplAttributes.InternalCall,
                    () => method.OverrideImplAttributes = null);
                var bytes = method.RawBytes;
                Reject("cached native code", () => { var changed = bytes.AsSpan().ToArray(); changed[0] ^= 1; method.RawBytes = new BinarySlice(changed); },
                    () => method.RawBytes = bytes);
                var pe = (PE)app.Binary;
                var rawOffset = checked((int)pe.MapVirtualAddressToRaw(method.UnderlyingPointer, false));
                var originalByte = pe.GetRawBinaryContent()[rawOffset];
                void WriteNativeByte(byte value)
                {
                    var position = pe.BaseStream.Position;
                    try { pe.BaseStream.Position = rawOffset; pe.BaseStream.WriteByte(value); }
                    finally { pe.BaseStream.Position = position; }
                }
                Reject("fresh native code", () => WriteNativeByte((byte)(originalByte ^ 1)),
                    () => WriteNativeByte(originalByte));
                var interior = method.UnderlyingPointer + 1;
                Reject("interior managed entry", () => app.MethodsByAddress.Add(interior, [method]),
                    () => app.MethodsByAddress.Remove(interior));
                var binding = method.GetExtraData<object>("EnumIntegerConversionRecovery")!;
                var evidence = X64EnumIntegerConversionProof.GetEvidence(method)!;
                var resultRegister = returned.Register;
                Reject("lost evidence and renamed conversion results", () =>
                {
                    method.PutExtraData<object>("EnumIntegerConversionRecovery", null!);
                    method.PutExtraData<X64EnumIntegerConversionProof.Proof>(X64EnumIntegerConversionProof.EvidenceKey, null!);
                    capture.Register = new Register(null, "ordinary_enum_capture", captureRegister.Version);
                    if (!ReferenceEquals(returned, capture))
                        returned.Register = new Register(null, "ordinary_enum_result", resultRegister.Version);
                    Assert.That(EnumIntegerConversionRecovery.HasEvidence(method), Is.True, label);
                }, () =>
                {
                    capture.Register = captureRegister; returned.Register = resultRegister;
                    method.PutExtraData("EnumIntegerConversionRecovery", binding);
                    method.PutExtraData(X64EnumIntegerConversionProof.EvidenceKey, evidence);
                });
                Assert.That(mutations, Is.EqualTo(original.ConvertSign ? 46 : 43), label + ": mutation denominator");
                IlGenerator.GenerateIl(method, definition);
                Assert.That(definition.Parameters[0].ParameterType.FullName, Is.EqualTo(original.Storage.Enum.FullName), label);
                var code = definition.CilMethodBody!.Instructions;
                var narrowOpcode = (original.Native.Width, original.Native.Signed) switch
                {
                    (8, true) => CilOpCodes.Conv_I1, (8, false) => CilOpCodes.Conv_U1,
                    (16, true) => CilOpCodes.Conv_I2, _ => CilOpCodes.Conv_U2,
                };
                Assert.That(code.Count(operation => operation.OpCode == narrowOpcode), Is.EqualTo(1), label);
                Assert.That(code.Count(operation => operation.OpCode == CilOpCodes.Conv_I4 || operation.OpCode == CilOpCodes.Conv_U4),
                    Is.EqualTo(original.ConvertSign ? 1 : 0), label);
                Assert.That(code.Any(operation => operation.OpCode == CilOpCodes.Conv_I8 || operation.OpCode == CilOpCodes.Conv_U8), Is.False, label);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
