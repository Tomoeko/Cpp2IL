using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AssetRipper.Primitives;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64ReferenceArraySearchFixtureTests
{
    private MethodAnalysisContext[] _methods = [];

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_REFERENCE_ARRAY_SEARCH_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_REFERENCE_ARRAY_SEARCH_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _methods = app.GetAssemblyByName("ReferenceArraySearchFixture")!.Types
            .Where(type => type.Name is "SearchHolder" or "PaddedSearchHolder")
            .SelectMany(type => type.Methods.Where(method => method.Name is "Find" or "Contains")).ToArray();
        Assert.That(_methods, Has.Length.EqualTo(4));
        foreach (var method in _methods) Accept(method);
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("SearchHolder", "Find")]
    [TestCase("SearchHolder", "Contains")]
    [TestCase("PaddedSearchHolder", "Find")]
    [TestCase("PaddedSearchHolder", "Contains")]
    public void ExactSearchRetainsCapturedLengthElementAndSignedFirstMatch(string owner, string name)
    {
        var method = Method(owner, name);
        var proof = Accept(method);
        Assert.Multiple(() =>
        {
            Assert.That(proof.Native.CapturesElement, Is.True);
            Assert.That(proof.Native.ReturnsBoolean, Is.EqualTo(name == "Contains"));
            Assert.That(proof.Native.NullCall, Is.Null);
            Assert.That(proof.KeyField.Name, Is.EqualTo("Key"));
            Assert.That(proof.KeyField.FieldType, Is.SameAs(method.AppContext.SystemTypes.SystemInt32Type));
        });
        var instructions = Definition(method).CilMethodBody!.Instructions.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldlen), Is.EqualTo(1));
            Assert.That(instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldelem_Ref), Is.EqualTo(1));
            Assert.That(instructions.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld), Is.EqualTo(2));
            Assert.That(instructions.Count(instruction => instruction.OpCode == CilOpCodes.Clt), Is.EqualTo(name == "Contains" ? 1 : 0));
            Assert.That(instructions.Any(instruction => instruction.OpCode == CilOpCodes.Add_Ovf), Is.False);
        });
        var length = Array.FindIndex(instructions, instruction => instruction.OpCode == CilOpCodes.Ldlen);
        Assert.That(instructions[length + 1].OpCode, Is.EqualTo(CilOpCodes.Conv_I4));
        Assert.That(instructions[length + 2].OpCode, Is.EqualTo(CilOpCodes.Stloc));
        var backedge = instructions.Single(instruction => instruction.OpCode == CilOpCodes.Br);
        var loop = ((CilInstructionLabel)backedge.Operand!).Instruction!;
        Assert.That(Array.IndexOf(instructions, loop), Is.GreaterThan(length + 2),
            "The backedge reuses the captured length instead of rereading it.");
        var match = instructions.Single(instruction => instruction.OpCode == CilOpCodes.Beq);
        var result = ((CilInstructionLabel)match.Operand!).Instruction!;
        var resultIndex = Array.IndexOf(instructions, result);
        Assert.That(result.OpCode, Is.EqualTo(CilOpCodes.Ldloc));
        Assert.That(instructions[resultIndex + 1].OpCode,
            Is.EqualTo(name == "Contains" ? CilOpCodes.Ldc_I4_0 : CilOpCodes.Ret));
    }

    [TestCase("key-unsigned")]
    [TestCase("key-offset")]
    [TestCase("key-modifiers")]
    [TestCase("key-byref")]
    [TestCase("key-data")]
    [TestCase("array-type")]
    [TestCase("array-offset")]
    [TestCase("array-modifiers")]
    [TestCase("array-data")]
    [TestCase("array-unregistered-element")]
    [TestCase("element-modifiers")]
    [TestCase("element-data")]
    [TestCase("element-pointer")]
    [TestCase("array-cycle")]
    [TestCase("parameter-unsigned")]
    [TestCase("parameter-modifiers")]
    [TestCase("parameter-data")]
    [TestCase("return-type")]
    [TestCase("return-data")]
    [TestCase("owner-base")]
    [TestCase("owner-data")]
    [TestCase("owner-size")]
    [TestCase("element-size")]
    [TestCase("element-name")]
    [TestCase("neighbor-overlap")]
    public void ChangedDeclarationAndStorageFactsInvalidateAdmittedSearch(string mutation)
    {
        var method = Method("PaddedSearchHolder", "Find");
        var proof = Accept(method);
        var app = method.AppContext;
        var owner = method.DeclaringType!;
        var element = proof.KeyField.DeclaringType;
        var rawKey = proof.KeyField.BackingData!.Field.RawFieldType!;
        var rawArray = proof.ArrayField.BackingData!.Field.RawFieldType!;
        var rawElement = rawArray.GetEncapsulatedType();
        var rawParameter = method.Parameters[0].Definition!.RawType!;
        var undo = new List<Action>();
        try
        {
            switch (mutation)
            {
                case "key-unsigned": Replace(value => proof.KeyField.OverrideFieldType = value,
                    proof.KeyField.OverrideFieldType, app.SystemTypes.SystemUInt32Type, undo); break;
                case "key-offset": Replace(value => proof.KeyField.OverrideOffset = value,
                    proof.KeyField.OverrideOffset, (int?)(proof.KeyField.Offset + 4), undo); break;
                case "key-modifiers": Replace(value => rawKey.NumMods = value, rawKey.NumMods, 1U, undo); break;
                case "key-byref": Replace(value => rawKey.Byref = value, rawKey.Byref, 1U, undo); break;
                case "key-data": Replace(value => rawKey.Data = value, rawKey.Data, null!, undo); break;
                case "array-type": Replace(value => rawArray.Type = value, rawArray.Type,
                    Il2CppTypeEnum.IL2CPP_TYPE_ARRAY, undo); break;
                case "array-offset": Replace(value => proof.ArrayField.OverrideOffset = value,
                    proof.ArrayField.OverrideOffset, (int?)(proof.ArrayField.Offset + 8), undo); break;
                case "array-modifiers": Replace(value => rawArray.NumMods = value, rawArray.NumMods, 1U, undo); break;
                case "array-data": Replace(value => rawArray.Data = value, rawArray.Data, null!, undo); break;
                case "array-unregistered-element": Replace(value => rawArray.Data.Dummy = value,
                    rawArray.Data.Dummy, ulong.MaxValue, undo); break;
                case "element-modifiers": Replace(value => rawElement.NumMods = value, rawElement.NumMods, 1U, undo); break;
                case "element-data": Replace(value => rawElement.Data = value, rawElement.Data, null!, undo); break;
                case "element-pointer": Replace(value => rawElement.Type = value, rawElement.Type,
                    Il2CppTypeEnum.IL2CPP_TYPE_PTR, undo); break;
                case "array-cycle":
                    Replace(value => rawElement.Type = value, rawElement.Type, Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY, undo);
                    Replace(value => rawElement.Data.Dummy = value, rawElement.Data.Dummy, rawArray.Data.Type, undo);
                    break;
                case "parameter-unsigned": Replace(value => method.Parameters[0].OverrideParameterType = value,
                    method.Parameters[0].OverrideParameterType, app.SystemTypes.SystemUInt32Type, undo); break;
                case "parameter-modifiers": Replace(value => rawParameter.NumMods = value, rawParameter.NumMods, 1U, undo); break;
                case "parameter-data": Replace(value => rawParameter.Data = value, rawParameter.Data, null!, undo); break;
                case "return-type": Replace(value => method.OverrideReturnType = value,
                    method.OverrideReturnType, app.SystemTypes.SystemBooleanType, undo); break;
                case "return-data":
                    var rawReturn = method.Definition!.RawReturnType!;
                    Replace(value => rawReturn.Data = value, rawReturn.Data, null!, undo);
                    break;
                case "owner-base": Replace(value => owner.OverrideBaseType = value,
                    owner.OverrideBaseType, element, undo); break;
                case "owner-data": Replace(value => owner.Definition!.RawType.Data = value,
                    owner.Definition!.RawType.Data, null!, undo); break;
                case "owner-size": ChangeInstanceSize(owner, undo); break;
                case "element-size": ChangeInstanceSize(element, undo); break;
                case "element-name": Replace(value => element.OverrideName = value,
                    element.OverrideName, "ChangedSearchElement", undo); break;
                case "neighbor-overlap":
                    var neighbor = owner.Fields.Single(field => field.Name == "Neighbor");
                    Replace(value => neighbor.OverrideOffset = value, neighbor.OverrideOffset,
                        (int?)proof.ArrayField.Offset, undo);
                    break;
            }
            Reject(method);
        }
        finally
        {
            for (var index = undo.Count - 1; index >= 0; index--) undo[index]();
        }
        Accept(method);
    }

    [TestCase("field")]
    [TestCase("method")]
    [TestCase("cctor")]
    public void CurrentMembersMustStillBeTheOriginalOwnedDeclarations(string mutation)
    {
        var method = Method("PaddedSearchHolder", "Find");
        Accept(method);
        var owner = method.DeclaringType!;
        var fields = owner.Fields.ToArray();
        var methods = owner.Methods.ToArray();
        try
        {
            if (mutation == "field") owner.Fields.RemoveAt(0);
            else if (mutation == "method") owner.Methods.RemoveAt(0);
            else owner.Methods.Add(new InjectedMethodAnalysisContext(owner, ".cctor",
                method.AppContext.SystemTypes.SystemVoidType,
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName |
                MethodAttributes.RTSpecialName, []));
            Reject(method);
        }
        finally
        {
            owner.Fields.Clear();
            owner.Fields.AddRange(fields);
            owner.Methods.Clear();
            owner.Methods.AddRange(methods);
        }
        Accept(method);
    }

    [TestCase("cache")]
    [TestCase("body")]
    [TestCase("bounds-helper")]
    public void NativeCallerAndHelperBytesRemainAuthoritative(string mutation)
    {
        var method = Method("SearchHolder", "Find");
        var proof = Accept(method);
        var cache = method.RawBytes;
        var pe = (PE)method.AppContext.Binary;
        var offset = checked((int)pe.MapVirtualAddressToRaw(mutation == "bounds-helper"
            ? proof.Native.BoundsCall.NearBranchTarget : method.UnderlyingPointer, false));
        var original = pe.GetRawBinaryContent()[offset];
        var position = pe.BaseStream.Position;
        try
        {
            if (mutation == "cache")
            {
                var changed = cache.ToArray();
                changed[0] ^= 1;
                method.RawBytes = new BinarySlice(changed);
            }
            else
            {
                pe.BaseStream.Position = offset;
                pe.BaseStream.WriteByte((byte)(original ^ 1));
            }
            Reject(method);
        }
        finally
        {
            method.RawBytes = cache;
            pe.BaseStream.Position = offset;
            pe.BaseStream.WriteByte(original);
            pe.BaseStream.Position = position;
        }
        Accept(method);
    }

    [Test]
    public void EmptyCacheCanBeInitializedWithoutReplacingAnExistingCache()
    {
        var method = Method("SearchHolder", "Contains");
        Accept(method);
        var cache = method.RawBytes;
        try
        {
            method.RawBytes = BinarySlice.Empty;
            Accept(method);
            Assert.That(method.RawBytes.Length, Is.GreaterThan(0));
        }
        finally { method.RawBytes = cache; }
        Accept(method);
    }

    [Test]
    public void RemovingAdmittedEvidenceCannotFallBackToAnUnprovedBody()
    {
        var method = Method("SearchHolder", "Find");
        var proof = Accept(method);
        method.PutExtraData<X64ReferenceArraySearchProof.Proof>(X64ReferenceArraySearchProof.EvidenceKey, null!);
        try
        {
            Assert.That(X64ReferenceArraySearchProof.Find(method), Is.Not.Null);
            Reject(method);
        }
        finally { method.PutExtraData(X64ReferenceArraySearchProof.EvidenceKey, proof); }
        Accept(method);
    }

    [Test]
    public void RemovingOnlyTheAdmissionMarkCannotHideSavedEvidence()
    {
        var method = Method("SearchHolder", "Find");
        Accept(method);
        var table = (ConditionalWeakTable<MethodAnalysisContext, HashSet<string>>)
            typeof(NativeRecoveryProofTracker).GetField("Proofs", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null)!;
        Assert.That(table.TryGetValue(method, out var recorded), Is.True);
        Assert.That(recorded!.Remove(X64ReferenceArraySearchProof.EvidenceKey), Is.True);
        try { Reject(method); }
        finally { NativeRecoveryProofTracker.Mark(method, X64ReferenceArraySearchProof.EvidenceKey); }
        Accept(method);
    }

    private MethodAnalysisContext Method(string owner, string name) => _methods.Single(method =>
        method.DeclaringType!.Name == owner && method.Name == name);

    private static MethodDefinition Definition(MethodAnalysisContext method) =>
        method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;

    private static X64ReferenceArraySearchProof.Proof Accept(MethodAnalysisContext method)
    {
        Assert.That(X64ReferenceArraySearchProof.TryAuthenticate(method, out var proof), Is.True,
            method.FullNameWithSignature);
        Assert.That(NativeRecoveryProofTracker.Has(method, X64ReferenceArraySearchProof.EvidenceKey), Is.True);
        Assert.That(X64ReferenceArraySearchRecovery.TryGenerate(method, Definition(method)), Is.True);
        return proof;
    }

    private static void Reject(MethodAnalysisContext method)
    {
        Assert.That(X64ReferenceArraySearchProof.TryAuthenticate(method, out _), Is.False);
        Assert.Throws<InvalidOperationException>(() =>
            X64ReferenceArraySearchRecovery.TryGenerate(method, Definition(method)));
    }

    private static void Replace<T>(Action<T> replace, T original, T changed, List<Action> undo)
    {
        undo.Add(() => replace(original));
        replace(changed);
    }

    private static void ChangeInstanceSize(TypeAnalysisContext type, List<Action> undo)
    {
        // RawSizes is read afresh from the PE. Mutating a temporary RawSizes
        // object would leave the actual layout evidence unchanged.
        var pe = (PE)type.AppContext.Binary;
        var pointer = pe.TypeDefinitionSizePointers[type.Definition!.TypeIndex.Value];
        var offset = checked((int)pe.MapVirtualAddressToRaw(pointer, false));
        var original = pe.GetRawBinaryContent().Slice(offset, 4).ToArray();
        var position = pe.BaseStream.Position;
        undo.Add(() =>
        {
            pe.BaseStream.Position = offset;
            pe.BaseStream.Write(original);
            pe.BaseStream.Position = position;
        });
        pe.BaseStream.Position = offset;
        pe.BaseStream.Write(BitConverter.GetBytes(16U));
        Assert.That(type.Definition.RawSizes.instance_size, Is.EqualTo(16U));
    }
}
