using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class CallBeforeCaptureBooleanStoreFixtureTests
{
    [Test]
    public void ProtectedInheritedSnapshotRetainsEarlierCallAndNullFailure()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_CALL_BEFORE_CAPTURE_BOOLEAN_STORE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CALL_BEFORE_CAPTURE_BOOLEAN_STORE_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var assembly = app.GetAssemblyByName("CallBeforeCaptureBooleanStoreFixture")!;
            var owner = assembly.Types.Single(type => type.Name == "StoreOwner");
            var baseType = assembly.Types.Single(type => type.Name == "StoreBase");
            var target = assembly.Types.Single(type => type.Name == "StoreTarget");
            var method = owner.Methods.Single(candidate => candidate.Name == "SetAfterTouch");
            var body = X64NativeInstructionReader.ReadRootBody(method)!;
            var unwind = X64UnwindProof.ForApplication(app)!;
            Assert.That(body, Has.Length.EqualTo(14));
            Assert.Multiple(() =>
            {
                Assert.That(unwind.MatchesUnwind(method.UnderlyingPointer, body[^1].NextIP,
                    6, 0, [6, 0x32, 2, 0x30]), Is.True);
                Assert.That(body[0].NextIP - method.UnderlyingPointer, Is.EqualTo(2));
                Assert.That(body[1].NextIP - method.UnderlyingPointer, Is.EqualTo(6));
                Assert.That(body[4].Code, Is.EqualTo(Code.Call_rel32_64));
                Assert.That(body[5].Code, Is.EqualTo(Code.Mov_r64_rm64));
                Assert.That(body[7].NearBranchTarget, Is.EqualTo(body[12].IP));
                Assert.That(body[8].Code, Is.EqualTo(Code.Mov_rm8_imm8));
                Assert.That(body[12].Code, Is.EqualTo(Code.Call_rel32_64));
                Assert.That(body[13].Code, Is.EqualTo(Code.Int3));
                Assert.That(body[13].IP, Is.EqualTo(body[12].NextIP));
                Assert.That(X64NativePaddingProof.HasInt3Padding((LibCpp2IL.PE.PE)app.Binary,
                    body[12].NextIP, body[13].NextIP), Is.True);
                Assert.That(X86RuntimeNullThrowProof.TryIdentify(app,
                    body[12].NearBranchTarget), Is.Not.Null);
                Assert.That(X86RuntimeNullThrowProof.TryIdentify(app,
                    body[12].NearBranchTarget + 1), Is.Null);
                Assert.That(X86CallerExceptionRegionProof.Check(method, body.Take(13).ToArray(),
                    new HashSet<ulong> { body[12].IP }), Is.Null);
            });
            Assert.That(X64NativeRegisterAliasProof.IsAlias(body, body[5].IP,
                body[5].MemoryBase, NativeRegister.RCX), Is.True);
            Assert.That(X64NativeRegisterAliasProof.IsAliasFromFieldLoad(body,
                body[8].IP, body[8].MemoryBase, body[5].IP), Is.True);

            method.Analyze();
            Assert.That(method.AnalysisWarnings, Is.Empty);
            var graph = method.ControlFlowGraph!;
            Assert.That(graph.Instructions.Any(instruction =>
                instruction.OpCode == OpCode.RuntimeNullThrow), Is.False);
            var load = graph.Instructions.Single(instruction => instruction is
                { OpCode: OpCode.Move, Operands: [LocalVariable, FieldReference source] } &&
                ReferenceEquals(source.Field.DeclaringType, baseType));
            var receiver = (LocalVariable)load.Operands[0];
            var access = (FieldReference)load.Operands[1];
            Assert.That(ReferenceEquals(access.Local.Type, owner) && access.Local.IsThis, Is.True);
            Assert.That(access.Field.Visibility, Is.EqualTo(FieldAttributes.Family));
            Assert.That(ReferenceEquals(owner.BaseType, baseType), Is.True);
            Assert.That(FieldLoadReceiverProof.HasBoundRead(method, receiver, load), Is.True);
            Assert.That(load.NativeAddress, Is.EqualTo(body[5].IP));

            var marker = method.NullCheckedFieldAccesses.Single(item =>
                item.StoredValue is Immediate && ReferenceEquals(item.Field.DeclaringType, target));
            Assert.That(marker.IsValidFor(method), Is.True);
            Assert.That(marker.Operation.NativeAddress, Is.EqualTo(body[8].IP));
            Assert.That(marker.Operation.Operands[1], Is.TypeOf<Immediate>());
            Assert.That(((Immediate)marker.Operation.Operands[1]).Value, Is.EqualTo(1));
            var definition = method.GetExtraData<AsmResolver.DotNet.MethodDefinition>(
                "AsmResolverMethod")!;
            Assert.That(() => IlGenerator.GenerateIl(method, definition), Throws.Nothing);

            void Reject(Action mutate, Action restore)
            {
                try
                {
                    mutate();
                    Assert.That(FieldLoadReceiverProof.HasBoundRead(method, receiver, load), Is.False);
                    Assert.That(marker.IsValidFor(method), Is.False);
                }
                finally { restore(); }
                Assert.That(FieldLoadReceiverProof.HasBoundRead(method, receiver, load), Is.True);
                Assert.That(marker.IsValidFor(method), Is.True);
            }

            Reject(() => access.Field.OverrideAttributes = FieldAttributes.Private,
                () => access.Field.OverrideAttributes = null);
            Reject(() => access.Field.OverrideAttributes = FieldAttributes.FamORAssem,
                () => access.Field.OverrideAttributes = null);
            Reject(() => owner.OverrideBaseType = app.SystemTypes.SystemObjectType,
                () => owner.OverrideBaseType = null);
            Reject(() => method.OverrideReturnType = app.SystemTypes.SystemVoidType,
                () => method.OverrideReturnType = null);
            var callerReturn = method.Definition!.RawReturnType!;
            Reject(() => callerReturn.Type = Il2CppTypeEnum.IL2CPP_TYPE_I4,
                () => callerReturn.Type = Il2CppTypeEnum.IL2CPP_TYPE_VOID);
            var originalOffset = access.Offset;
            Reject(() => access.Offset++, () => access.Offset = originalOffset);
            var originalField = access.Field;
            Reject(() => access.Field = owner.Fields.Single(field => field.Name == "Other"),
                () => access.Field = originalField);
            var originalAddress = load.NativeAddress;
            Reject(() => load.NativeAddress = originalAddress + 1,
                () => load.NativeAddress = originalAddress);
            var originalBytes = method.RawBytes;
            Reject(() =>
            {
                var changed = originalBytes.AsSpan().ToArray();
                var index = checked((int)(body[5].IP - method.UnderlyingPointer)) + body[5].Length - 1;
                changed[index] ^= 1;
                method.RawBytes = new BinarySlice(changed);
            }, () => method.RawBytes = originalBytes);

            var block = graph.FindBlockByInstruction(load)!;
            var call = block.Instructions.Single(instruction =>
                instruction.OpCode == OpCode.CallVoid && instruction.NativeAddress == body[4].IP);
            Assert.That(call.Operands.Count, Is.EqualTo(2));
            var called = (MethodAnalysisContext)call.Operands[0];
            Reject(() => called.OverrideAttributes = MethodAttributes.Private,
                () => called.OverrideAttributes = null);
            Reject(() => called.OverrideImplAttributes = MethodImplAttributes.IL,
                () => called.OverrideImplAttributes = null);
            Reject(() => called.OverrideReturnType = app.SystemTypes.SystemVoidType,
                () => called.OverrideReturnType = null);
            var calleeReturn = called.Definition!.RawReturnType!;
            Reject(() => calleeReturn.Type = Il2CppTypeEnum.IL2CPP_TYPE_I4,
                () => calleeReturn.Type = Il2CppTypeEnum.IL2CPP_TYPE_VOID);
            var callOperands = call.Operands.ToArray();
            Reject(() => call.AddOperands([new Immediate(1)]),
                () => call.SetOperands(callOperands.ToList()));
            var loadPosition = block.Instructions.IndexOf(load);
            Reject(() =>
            {
                block.Instructions.Remove(load);
                block.Instructions.Insert(block.Instructions.IndexOf(call), load);
            }, () =>
            {
                block.Instructions.Remove(load);
                block.Instructions.Insert(loadPosition, load);
            });
            Assert.That(() => IlGenerator.GenerateIl(method, definition), Throws.Nothing);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
