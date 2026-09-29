using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using MethodDefinition = AsmResolver.DotNet.MethodDefinition;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class OpenGenericEarlyFieldFixtureTests
{
    [Test]
    public void ExactOpenReceiverLeavesPreserveOnlyFieldsBeforeUnknownGenericStorage()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_OPEN_GENERIC_EARLY_FIELD_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_OPEN_GENERIC_EARLY_FIELD_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var owner = app.GetAssemblyByName("OpenGenericEarlyFieldFixture")!.Types.Single(type =>
                type.Name.StartsWith("OpenEarlyState", StringComparison.Ordinal));
            Assert.That(owner.Fields.Select(field => field.BackingData!.Field.RawFieldType!.Type),
                Is.EqualTo(new[] { Il2CppTypeEnum.IL2CPP_TYPE_I4, Il2CppTypeEnum.IL2CPP_TYPE_OBJECT,
                    Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN, Il2CppTypeEnum.IL2CPP_TYPE_VAR }));
            Assert.That(owner.Methods, Has.Count.EqualTo(4));
            foreach (var method in owner.Methods)
            {
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty, method.Name);
                var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, output), method.Name);
                if (method.Name == ".ctor")
                {
                    Assert.That(OpenGenericEarlyFieldProof.HasEvidence(method), Is.False);
                    continue;
                }
                Assert.That(OpenGenericEarlyFieldProof.HasEvidence(method), Is.True, method.Name);
                Assert.That(OpenGenericEarlyFieldProof.IsValidFor(method), Is.True, method.Name);
                Assert.That(output.CilMethodBody!.Instructions.Count(instruction =>
                    instruction.OpCode == CilOpCodes.Ldfld), Is.EqualTo(1), method.Name);
                RejectMutations(method, owner, output);
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectMutations(MethodAnalysisContext method, TypeAnalysisContext owner,
        MethodDefinition output)
    {
        var graph = method.ControlFlowGraph!;
        var body = graph.Blocks.Single(block => block != graph.EntryBlock && block != graph.ExitBlock);
        Assert.That(body.Instructions.ToArray(), Has.Length.EqualTo(2));
        var read = body.Instructions[0];
        var ret = body.Instructions[1];
        var access = (FieldReference)read.Operands[1];
        var projected = (ConcreteGenericFieldAnalysisContext)access.Field;
        var baseField = projected.BaseFieldContext;
        var incoming = access.Local;
        var value = (LocalVariable)read.Operands[0];
        var proof = method.GetExtraData<OpenGenericEarlyFieldProof.Evidence>(
            OpenGenericEarlyFieldProof.EvidenceKey)!;
        var rawBytes = method.RawBytes;
        var fields = owner.Fields.ToArray();
        var later = fields.Single(field => field.Name == "Later");
        var pe = (PE)method.AppContext.Binary;
        void Reject(string name, Action mutate, Action restore)
        {
            mutate();
            try
            {
                Assert.That(OpenGenericEarlyFieldProof.HasEvidence(method), Is.True, name);
                Assert.That(OpenGenericEarlyFieldProof.IsValidFor(method), Is.False, name);
                Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, output), name);
            }
            finally { restore(); }
            Assert.That(OpenGenericEarlyFieldProof.IsValidFor(method), Is.True, "restored " + name);
        }
        Reject("VAR moved ahead of native target", () =>
        {
            owner.Fields.Remove(later); owner.Fields.Insert(0, later);
        }, () => { owner.Fields.Clear(); owner.Fields.AddRange(fields); });
        var savedBase = owner.OverrideBaseType;
        Reject("original parent changed", () => owner.OverrideBaseType = method.AppContext.SystemTypes.SystemValueTypeType,
            () => owner.OverrideBaseType = savedBase);
        var savedParentIndex = owner.Definition!.ParentIndex;
        Reject("raw parent changed", () => owner.Definition.ParentIndex = owner.Definition.ByvalTypeIndex,
            () => owner.Definition.ParentIndex = savedParentIndex);
        var savedOwnerFlags = owner.Definition.Flags;
        Reject("owner layout changed", () => owner.Definition.Flags |= (uint)System.Reflection.TypeAttributes.ExplicitLayout,
            () => owner.Definition.Flags = savedOwnerFlags);
        var savedFieldType = baseField.OverrideFieldType;
        Reject("consumed field type changed", () => baseField.OverrideFieldType = method.AppContext.SystemTypes.SystemInt64Type,
            () => baseField.OverrideFieldType = savedFieldType);
        var savedRawType = baseField.BackingData!.Field.RawFieldType!.Type;
        Reject("raw field storage changed", () => baseField.BackingData.Field.RawFieldType!.Type = Il2CppTypeEnum.IL2CPP_TYPE_VAR,
            () => baseField.BackingData.Field.RawFieldType!.Type = savedRawType);
        var rawReturn = method.Definition!.RawReturnType!;
        var savedReturnKind = rawReturn.Type;
        var savedReturnData = rawReturn.Data.Dummy;
        Assert.That(pe.TryGetTypeVirtualAddress(rawReturn, out var returnDescriptor), Is.True);
        Reject("cached recursive return descriptor", () =>
        {
            rawReturn.Type = Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY;
            rawReturn.Data.Dummy = returnDescriptor;
        }, () => { rawReturn.Type = savedReturnKind; rawReturn.Data.Dummy = savedReturnData; });
        var savedFieldIndex = baseField.BackingData.Field.typeIndex;
        Reject("raw field type index changed", () => baseField.BackingData.Field.typeIndex = later.BackingData!.Field.typeIndex,
            () => baseField.BackingData.Field.typeIndex = savedFieldIndex);
        var savedReadSite = read.NativeAddress;
        Reject("native read site changed", () => read.NativeAddress = savedReadSite + 1,
            () => read.NativeAddress = savedReadSite);
        var savedReturnSite = ret.NativeAddress;
        Reject("native return site changed", () => ret.NativeAddress = savedReturnSite + 1,
            () => ret.NativeAddress = savedReturnSite);
        var savedOffset = access.Offset;
        Reject("field offset changed", () => access.Offset = savedOffset + 1,
            () => access.Offset = savedOffset);
        var savedProjectedType = projected.OverrideFieldType;
        Reject("projected field type changed", () => projected.OverrideFieldType = method.AppContext.SystemTypes.SystemInt64Type,
            () => projected.OverrideFieldType = savedProjectedType);
        var savedIncomingType = incoming.Type;
        Reject("incoming receiver type changed", () => incoming.Type = method.AppContext.SystemTypes.SystemObjectType,
            () => incoming.Type = savedIncomingType);
        var savedIncomingSlot = incoming.Register;
        Reject("incoming receiver slot changed", () => incoming.Register = new Register(null, "rdx", -1),
            () => incoming.Register = savedIncomingSlot);
        Reject("missing original incoming", () => method.ParameterLocals.Remove(incoming),
            () => method.ParameterLocals.Insert(0, incoming));
        var savedResultType = value.Type;
        Reject("result type changed", () => value.Type = method.AppContext.SystemTypes.SystemInt64Type,
            () => value.Type = savedResultType);
        Reject("extra managed effect", () => body.Instructions.Insert(1, read),
            () => body.Instructions.RemoveAt(1));
        Reject("lost mutable evidence", () => method.PutExtraData<OpenGenericEarlyFieldProof.Evidence>(
                OpenGenericEarlyFieldProof.EvidenceKey, null!),
            () => method.PutExtraData(OpenGenericEarlyFieldProof.EvidenceKey, proof));
        Reject("changed cached native code", () =>
        {
            var altered = rawBytes.AsSpan().ToArray(); altered[0] ^= 1;
            method.RawBytes = new BinarySlice(altered);
        }, () => method.RawBytes = rawBytes);
        var nativeOffset = pe.MapVirtualAddressToRaw(method.UnderlyingPointer);
        var savedByte = pe.GetByteAtRawAddress((ulong)nativeOffset);
        var position = pe.BaseStream.Position;
        Reject("changed current PE native code", () =>
        {
            pe.BaseStream.Position = nativeOffset;
            pe.BaseStream.WriteByte((byte)(savedByte ^ 1));
        }, () =>
        {
            pe.BaseStream.Position = nativeOffset;
            pe.BaseStream.WriteByte(savedByte);
            pe.BaseStream.Position = position;
        });
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, output));
    }
}
