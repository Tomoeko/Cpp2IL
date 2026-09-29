using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using MethodDefinition = AsmResolver.DotNet.MethodDefinition;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class OpenGenericPrefixOperationsFixtureTests
{
    [Test]
    public void MutatedOriginalPrefixDescriptorCannotFallBackToUnprovedLayout()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_OPEN_GENERIC_PREFIX_OPERATIONS_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_OPEN_GENERIC_PREFIX_OPERATIONS_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
                Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var owner = app.GetAssemblyByName("OpenGenericPrefixOperationsFixture")!.Types.Single(type =>
                type.Name.StartsWith("PrefixState", StringComparison.Ordinal));
            var method = owner.Methods.Single(candidate => candidate.Name == "UpdateAndSum");
            var rawField = owner.Fields.Single(field => field.Name == "First").BackingData!.Field.RawFieldType!;
            var originalKind = rawField.Type;
            Assert.That(originalKind, Is.EqualTo(Il2CppTypeEnum.IL2CPP_TYPE_I4));
            try
            {
                rawField.Type = Il2CppTypeEnum.IL2CPP_TYPE_I8;
                Assert.DoesNotThrow(() => method.Analyze());
                Assert.That(OpenGenericPrefixFieldLayoutProof.HasEvidence(method), Is.False);
                var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, output));
            }
            finally { rawField.Type = originalKind; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void ExactOpenGenericPrefixOperationsBindOriginalFieldsAndNativeBody()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_OPEN_GENERIC_PREFIX_OPERATIONS_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_OPEN_GENERIC_PREFIX_OPERATIONS_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
                Path.Combine(input!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var owner = app.GetAssemblyByName("OpenGenericPrefixOperationsFixture")!.Types.Single(type =>
                type.Name.StartsWith("PrefixState", StringComparison.Ordinal));
            Assert.That(owner.Methods, Has.Count.EqualTo(2));
            foreach (var method in owner.Methods)
            {
                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty, method.Name);
                var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, output), method.Name);
                Assert.That(output.CilMethodBody, Is.Not.Null, method.Name);
            }
            var update = owner.Methods.Single(method => method.Name == "UpdateAndSum");
            Assert.That(OpenGenericPrefixFieldLayoutProof.HasEvidence(update), Is.True);
            Assert.That(OpenGenericPrefixFieldLayoutProof.IsValidFor(update), Is.True);
            var definition = update.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            var il = definition.CilMethodBody!.Instructions;
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld), Is.GreaterThanOrEqualTo(2));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stfld), Is.EqualTo(1));
            RejectMutations(update, owner, definition);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectMutations(MethodAnalysisContext method, TypeAnalysisContext owner,
        MethodDefinition output)
    {
        var fieldOperand = method.ControlFlowGraph!.Instructions.SelectMany(operation => operation.Operands)
            .OfType<FieldReference>().First();
        var originalField = fieldOperand.Field;
        var otherField = owner.Fields.Single(field => field.Name ==
            (originalField.Name == "Second" ? "First" : "Second"));
        var rawArgument = method.Parameters.Single().Definition!.RawType!;
        var argumentKind = rawArgument.Type;
        var rawReturn = method.Definition!.RawReturnType!;
        var returnKind = rawReturn.Type;
        var order = owner.Fields.ToArray();
        var later = owner.Fields.Single(field => field.Name == "Later");
        var pe = (PE)method.AppContext.Binary;
        var nativeOffset = pe.MapVirtualAddressToRaw(method.UnderlyingPointer);
        var nativeByte = pe.GetByteAtRawAddress((ulong)nativeOffset);
        var originalPosition = pe.BaseStream.Position;

        void Reject(string name, Action mutate, Action restore)
        {
            mutate();
            try
            {
                Assert.That(OpenGenericPrefixFieldLayoutProof.HasEvidence(method), Is.True, name);
                Assert.That(OpenGenericPrefixFieldLayoutProof.IsValidFor(method), Is.False, name);
                Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(method, output), name);
            }
            finally { restore(); }
            Assert.That(OpenGenericPrefixFieldLayoutProof.IsValidFor(method), Is.True, "restored " + name);
        }

        Reject("unknown generic storage moved before prefix", () =>
        {
            owner.Fields.Remove(later);
            owner.Fields.Insert(0, later);
        }, () => { owner.Fields.Clear(); owner.Fields.AddRange(order); });
        Reject("bound field changed", () => fieldOperand.Field = otherField,
            () => fieldOperand.Field = originalField);
        Reject("original argument kind changed", () => rawArgument.Type = Il2CppTypeEnum.IL2CPP_TYPE_I8,
            () => rawArgument.Type = argumentKind);
        Reject("original return kind changed", () => rawReturn.Type = Il2CppTypeEnum.IL2CPP_TYPE_U4,
            () => rawReturn.Type = returnKind);
        Reject("original native instruction changed", () =>
        {
            pe.BaseStream.Position = nativeOffset;
            pe.BaseStream.WriteByte((byte)(nativeByte ^ 1));
        }, () =>
        {
            pe.BaseStream.Position = nativeOffset;
            pe.BaseStream.WriteByte(nativeByte);
            pe.BaseStream.Position = originalPosition;
        });
        Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, output));
    }
}
