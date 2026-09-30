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
public class X64SignedFieldComparisonFixtureTests
{
    private MethodAnalysisContext[] _methods = [];

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_SIGNED_FIELD_COMPARISON_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_SIGNED_FIELD_COMPARISON_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _methods = app.GetAssemblyByName("SignedFieldComparisonFixture")!.Types.Single(type => type.Name == "ComparisonOwner")
            .Methods.Where(method => method.Name is "Compare" or "CompareAgain" or "ComparePadded").ToArray();
        Assert.That(_methods, Has.Length.EqualTo(3));
        foreach (var method in _methods) Accept(method);
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("Compare")]
    [TestCase("CompareAgain")]
    [TestCase("ComparePadded")]
    public void ExactPlayerCapturesBothSignedOperandsAndPreservesOrderedNullGuards(string name)
    {
        var method = Method(name);
        var proof = Accept(method);
        var il = Definition(method).CilMethodBody!.Instructions.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(proof.Native.CapturesFields, Is.True);
            Assert.That(proof.Field.Name, Is.EqualTo("Key"));
            Assert.That(proof.Field.Offset, Is.EqualTo(name == "ComparePadded" ? 88 : 16));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld), Is.EqualTo(2));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Brtrue), Is.EqualTo(2));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Throw), Is.EqualTo(2));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Cgt), Is.EqualTo(1));
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Sub || instruction.OpCode == CilOpCodes.Blt_Un ||
                instruction.OpCode == CilOpCodes.Cgt_Un), Is.False);
        });
        Assert.That(il[0].OpCode, Is.EqualTo(CilOpCodes.Ldarg_1));
        Assert.That(il[4].OpCode, Is.EqualTo(CilOpCodes.Ldarg_2));
        Assert.That(il[8].OpCode, Is.EqualTo(CilOpCodes.Ldarg_1));
    }

    [Test]
    public void LinkerFoldedManagedIdentitiesAreValidatedIndividually()
    {
        var first = Method("Compare");
        var second = Method("CompareAgain");
        Assert.That(second.UnderlyingPointer, Is.EqualTo(first.UnderlyingPointer));
        Accept(first);
        Accept(second);
        var old = second.Parameters[0].OverrideParameterType;
        try
        {
            second.Parameters[0].OverrideParameterType = first.AppContext.SystemTypes.SystemObjectType;
            Reject(first);
            Reject(second);
        }
        finally { second.Parameters[0].OverrideParameterType = old; }
        Accept(first);
        Accept(second);
    }

    [TestCase("field-unsigned")]
    [TestCase("field-offset")]
    [TestCase("field-modifiers")]
    [TestCase("field-data")]
    [TestCase("neighbor-overlap")]
    [TestCase("parameter-type")]
    [TestCase("parameter-byref")]
    [TestCase("parameter-data")]
    [TestCase("return-unsigned")]
    [TestCase("return-data")]
    [TestCase("item-base")]
    [TestCase("owner-cctor-bit")]
    [TestCase("owner-enclosing")]
    [TestCase("item-enclosing")]
    public void ChangedMetadataCannotReinterpretSignedStorageOrCapturedIdentity(string mutation)
    {
        var method = Method("ComparePadded");
        var proof = Accept(method);
        var app = method.AppContext;
        var item = proof.Field.DeclaringType;
        var field = proof.Field;
        var rawField = field.BackingData!.Field.RawFieldType!;
        var rawParameter = method.Parameters[0].Definition!.RawType!;
        var rawReturn = method.Definition!.RawReturnType!;
        var undo = new List<Action>();
        try
        {
            switch (mutation)
            {
                case "field-unsigned": Replace(value => field.OverrideFieldType = value, field.OverrideFieldType,
                    app.SystemTypes.SystemUInt32Type, undo); break;
                case "field-offset": Replace(value => field.OverrideOffset = value, field.OverrideOffset, (int?)92, undo); break;
                case "field-modifiers": Replace(value => rawField.NumMods = value, rawField.NumMods, 1U, undo); break;
                case "field-data": Replace(value => rawField.Data = value, rawField.Data, null!, undo); break;
                case "neighbor-overlap":
                    var neighbor = item.Fields.Single(member => member.Name == "Seventh");
                    Replace(value => neighbor.OverrideOffset = value, neighbor.OverrideOffset, (int?)88, undo); break;
                case "parameter-type": Replace(value => method.Parameters[0].OverrideParameterType = value,
                    method.Parameters[0].OverrideParameterType, app.SystemTypes.SystemObjectType, undo); break;
                case "parameter-byref": Replace(value => rawParameter.Byref = value, rawParameter.Byref, 1U, undo); break;
                case "parameter-data": Replace(value => rawParameter.Data = value, rawParameter.Data, null!, undo); break;
                case "return-unsigned": Replace(value => method.OverrideReturnType = value, method.OverrideReturnType,
                    app.SystemTypes.SystemUInt32Type, undo); break;
                case "return-data": Replace(value => rawReturn.Data = value, rawReturn.Data, null!, undo); break;
                case "item-base": Replace(value => item.OverrideBaseType = value, item.OverrideBaseType,
                    app.SystemTypes.SystemInt32Type, undo); break;
                case "owner-cctor-bit":
                    var definition = method.DeclaringType!.Definition!;
                    Replace(value => definition.Bitfield = value, definition.Bitfield, definition.Bitfield ^ 16U, undo); break;
                case "owner-enclosing":
                    var owner = method.DeclaringType!;
                    Replace(value => owner.DeclaringType = value, owner.DeclaringType, item, undo); break;
                case "item-enclosing":
                    Replace(value => item.DeclaringType = value, item.DeclaringType, method.DeclaringType, undo); break;
            }
            Reject(method);
        }
        finally { for (var index = undo.Count - 1; index >= 0; index--) undo[index](); }
        Accept(method);
    }

    [TestCase("field")]
    [TestCase("method")]
    [TestCase("cctor")]
    public void OriginalMemberListsCannotLoseDeclarationsOrGainInitializers(string mutation)
    {
        var method = Method("ComparePadded");
        Accept(method);
        var item = method.Parameters[0].ParameterType;
        var fields = item.Fields.ToArray();
        var methods = item.Methods.ToArray();
        try
        {
            if (mutation == "field") item.Fields.RemoveAt(0);
            else if (mutation == "method") item.Methods.RemoveAt(0);
            else item.Methods.Add(new InjectedMethodAnalysisContext(item, ".cctor", method.AppContext.SystemTypes.SystemVoidType,
                MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, []));
            Reject(method);
        }
        finally
        {
            item.Fields.Clear(); item.Fields.AddRange(fields);
            item.Methods.Clear(); item.Methods.AddRange(methods);
        }
        Accept(method);
    }

    [TestCase("cache")]
    [TestCase("body")]
    [TestCase("null-helper")]
    public void PhysicalNativeBytesAndExistingCacheMustAgree(string mutation)
    {
        var method = Method("ComparePadded");
        var proof = Accept(method);
        var cache = method.RawBytes;
        var pe = (PE)method.AppContext.Binary;
        var offset = checked((int)pe.MapVirtualAddressToRaw(mutation == "null-helper"
            ? proof.Native.NullCall.NearBranchTarget : method.UnderlyingPointer, false));
        var original = pe.GetRawBinaryContent()[offset];
        var position = pe.BaseStream.Position;
        try
        {
            if (mutation == "cache")
            {
                var bytes = cache.ToArray(); bytes[0] ^= 1;
                method.RawBytes = new BinarySlice(bytes);
            }
            else { pe.BaseStream.Position = offset; pe.BaseStream.WriteByte((byte)(original ^ 1)); }
            Reject(method);
        }
        finally
        {
            method.RawBytes = cache;
            pe.BaseStream.Position = offset; pe.BaseStream.WriteByte(original); pe.BaseStream.Position = position;
        }
        Accept(method);
    }

    [Test]
    public void LostAdmissionEvidenceOrMarkerCannotEnableFallback()
    {
        var method = Method("ComparePadded");
        var proof = Accept(method);
        method.PutExtraData<X64SignedFieldComparisonProof.Proof>(X64SignedFieldComparisonProof.EvidenceKey, null!);
        try { Reject(method); }
        finally { method.PutExtraData(X64SignedFieldComparisonProof.EvidenceKey, proof); }
        var table = (ConditionalWeakTable<MethodAnalysisContext, HashSet<string>>)
            typeof(NativeRecoveryProofTracker).GetField("Proofs", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.That(table.TryGetValue(method, out var recorded), Is.True);
        Assert.That(recorded!.Remove(X64SignedFieldComparisonProof.EvidenceKey), Is.True);
        try { Reject(method); }
        finally { NativeRecoveryProofTracker.Mark(method, X64SignedFieldComparisonProof.EvidenceKey); }
        Accept(method);
    }

    [Test]
    public void EmptyCacheInitializesAndRepeatedAuthenticationIsStable()
    {
        var method = Method("ComparePadded");
        Accept(method);
        var cache = method.RawBytes;
        try { method.RawBytes = BinarySlice.Empty; Accept(method); }
        finally { method.RawBytes = cache; }
        Accept(method);
    }

    private MethodAnalysisContext Method(string name) => _methods.Single(method => method.Name == name);
    private static MethodDefinition Definition(MethodAnalysisContext method) => method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
    private static X64SignedFieldComparisonProof.Proof Accept(MethodAnalysisContext method)
    {
        Assert.That(X64SignedFieldComparisonProof.TryAuthenticate(method, out var proof), Is.True);
        Assert.That(X64SignedFieldComparisonRecovery.TryGenerate(method, Definition(method)), Is.True);
        return proof;
    }
    private static void Reject(MethodAnalysisContext method)
    {
        Assert.That(X64SignedFieldComparisonProof.TryAuthenticate(method, out _), Is.False);
        Assert.That(() => X64SignedFieldComparisonRecovery.TryGenerate(method, Definition(method)), Throws.InvalidOperationException);
    }
    private static void Replace<T>(Action<T> set, T original, T changed, List<Action> undo)
    {
        undo.Add(() => set(original));
        set(changed);
    }
}
