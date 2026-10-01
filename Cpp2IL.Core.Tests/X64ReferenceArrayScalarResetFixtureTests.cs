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
public class X64ReferenceArrayScalarResetFixtureTests
{
    private MethodAnalysisContext[] _methods = [];

    [OneTimeSetUp]
    public void LoadExactPlayer()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_REFERENCE_ARRAY_SCALAR_RESET_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_REFERENCE_ARRAY_SCALAR_RESET_FIXTURE_INPUT to the neutral exact player input.");
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
            Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
            UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _methods = app.GetAssemblyByName("ReferenceArrayScalarResetFixture")!.Types
            .Single(type => type.Name == "ResetHolder").Methods
            .Where(method => method.Name is "ResetActive" or "ResetValues").ToArray();
        Assert.That(_methods, Has.Length.EqualTo(2));
        foreach (var method in _methods) Accept(method);
    }

    [OneTimeTearDown]
    public void Reset() => Cpp2IlApi.ResetInternalState();

    [TestCase("ResetActive", true)]
    [TestCase("ResetValues", false)]
    public void EmittedLoopRetainsCapturedElementsReloadOrderAndTypedPositiveZero(string name, bool reloaded)
    {
        var method = Method(name);
        var proof = Accept(method);
        var body = Definition(method).CilMethodBody!;
        var il = body.Instructions.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(proof.Native.Mode, Is.EqualTo(reloaded
                ? X64ReferenceArrayScalarResetProof.LoopMode.ReloadedLength
                : X64ReferenceArrayScalarResetProof.LoopMode.FixedCount));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld), Is.EqualTo(reloaded ? 3 : 1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldlen), Is.EqualTo(reloaded ? 1 : 0));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldelem_Ref), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Stfld), Is.EqualTo(reloaded ? 2 : 1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Newobj), Is.EqualTo(1));
            Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Throw), Is.EqualTo(1));
            Assert.That(il.Any(instruction => instruction.OpCode == CilOpCodes.Add_Ovf), Is.False);
            Assert.That(il.Where(instruction => instruction.OpCode == CilOpCodes.Ldc_R4)
                .All(instruction => BitConverter.SingleToInt32Bits((float)instruction.Operand!) == 0), Is.True);
        });
        var store = Array.FindLastIndex(il, instruction => instruction.OpCode == CilOpCodes.Stfld);
        var increment = Array.FindIndex(il, instruction => instruction.OpCode == CilOpCodes.Add);
        Assert.That(increment, Is.LessThan(store));
        if (reloaded)
        {
            Assert.That(il[2].OpCode, Is.EqualTo(CilOpCodes.Stfld));
            Assert.That(il[2].Operand, Is.SameAs(proof.MarkerField!.GetExtraData<IFieldDescriptor>("AsmResolverField")));
            var reads = il.Select((instruction, index) => (instruction, index))
                .Where(pair => pair.instruction.OpCode == CilOpCodes.Ldfld).Select(pair => pair.index).ToArray();
            var length = Array.FindIndex(il, instruction => instruction.OpCode == CilOpCodes.Ldlen);
            var item = Array.FindIndex(il, instruction => instruction.OpCode == CilOpCodes.Ldelem_Ref);
            Assert.That(reads[0], Is.LessThan(length));
            Assert.That(reads[1], Is.GreaterThan(length).And.LessThan(item));
            Assert.That(reads[2], Is.GreaterThan(store));
            var backedge = il.Single(instruction => instruction.OpCode == CilOpCodes.Br);
            Assert.That(Array.IndexOf(il, ((CilInstructionLabel)backedge.Operand!).Instruction!),
                Is.GreaterThan(reads[0]).And.LessThan(length));
        }
        else
        {
            Assert.That(proof.Native.Count, Is.EqualTo(7));
            var backedge = Array.FindIndex(il, instruction => instruction.OpCode == CilOpCodes.Blt);
            Assert.That(il[backedge - 1].Operand, Is.EqualTo(proof.Native.Count));
        }
    }

    [TestCase("array-type")]
    [TestCase("array-data")]
    [TestCase("array-modifiers")]
    [TestCase("array-byref")]
    [TestCase("array-offset")]
    [TestCase("array-element-data")]
    [TestCase("array-element-modifiers")]
    [TestCase("array-element-pointer")]
    [TestCase("array-cycle")]
    [TestCase("element-type")]
    [TestCase("element-data")]
    [TestCase("element-modifiers")]
    [TestCase("element-offset")]
    [TestCase("element-readonly")]
    [TestCase("marker-type")]
    [TestCase("marker-data")]
    [TestCase("marker-offset")]
    [TestCase("owner-data")]
    [TestCase("owner-base-data")]
    [TestCase("owner-base-modifiers")]
    [TestCase("element-base-data")]
    [TestCase("element-base-modifiers")]
    [TestCase("owner-base")]
    [TestCase("owner-size")]
    [TestCase("element-size")]
    [TestCase("neighbor-overlap")]
    [TestCase("return-type")]
    [TestCase("return-data")]
    [TestCase("method-synchronized")]
    [TestCase("field-token")]
    public void ChangedDeclarationsAndStorageCannotReuseAdmittedLoop(string mutation)
    {
        var method = Method("ResetActive");
        var proof = Accept(method);
        var app = method.AppContext;
        var owner = method.DeclaringType!;
        var element = proof.ElementField.DeclaringType;
        var rawArray = proof.ArrayField.BackingData!.Field.RawFieldType!;
        var rawArrayElement = rawArray.GetEncapsulatedType();
        var rawElement = proof.ElementField.BackingData!.Field.RawFieldType!;
        var rawMarker = proof.MarkerField!.BackingData!.Field.RawFieldType!;
        var undo = new List<Action>();
        try
        {
            switch (mutation)
            {
                case "array-type": Replace(value => rawArray.Type = value, rawArray.Type, Il2CppTypeEnum.IL2CPP_TYPE_ARRAY, undo); break;
                case "array-data": Replace(value => rawArray.Data = value, rawArray.Data, null!, undo); break;
                case "array-modifiers": Replace(value => rawArray.NumMods = value, rawArray.NumMods, 1U, undo); break;
                case "array-byref": Replace(value => rawArray.Byref = value, rawArray.Byref, 1U, undo); break;
                case "array-offset": Replace(value => proof.ArrayField.OverrideOffset = value,
                    proof.ArrayField.OverrideOffset, (int?)(proof.ArrayField.Offset + 8), undo); break;
                case "array-element-data": Replace(value => rawArrayElement.Data = value, rawArrayElement.Data, null!, undo); break;
                case "array-element-modifiers": Replace(value => rawArrayElement.NumMods = value, rawArrayElement.NumMods, 1U, undo); break;
                case "array-element-pointer": Replace(value => rawArrayElement.Type = value,
                    rawArrayElement.Type, Il2CppTypeEnum.IL2CPP_TYPE_PTR, undo); break;
                case "array-cycle":
                    Replace(value => rawArrayElement.Type = value, rawArrayElement.Type, Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY, undo);
                    Replace(value => rawArrayElement.Data.Dummy = value, rawArrayElement.Data.Dummy, rawArray.Data.Type, undo);
                    break;
                case "element-type": Replace(value => proof.ElementField.OverrideFieldType = value,
                    proof.ElementField.OverrideFieldType, app.SystemTypes.SystemByteType, undo); break;
                case "element-data": Replace(value => rawElement.Data = value, rawElement.Data, null!, undo); break;
                case "element-modifiers": Replace(value => rawElement.NumMods = value, rawElement.NumMods, 1U, undo); break;
                case "element-offset": Replace(value => proof.ElementField.OverrideOffset = value,
                    proof.ElementField.OverrideOffset, (int?)(proof.ElementField.Offset + 4), undo); break;
                case "element-readonly": Replace(value => proof.ElementField.OverrideAttributes = value,
                    proof.ElementField.OverrideAttributes, (FieldAttributes?)(proof.ElementField.Attributes | FieldAttributes.InitOnly), undo); break;
                case "marker-type": Replace(value => proof.MarkerField.OverrideFieldType = value,
                    proof.MarkerField.OverrideFieldType, app.SystemTypes.SystemInt32Type, undo); break;
                case "marker-data": Replace(value => rawMarker.Data = value, rawMarker.Data, null!, undo); break;
                case "marker-offset": Replace(value => proof.MarkerField.OverrideOffset = value,
                    proof.MarkerField.OverrideOffset, (int?)(proof.MarkerField.Offset + 4), undo); break;
                case "owner-data": Replace(value => owner.Definition!.RawType.Data = value,
                    owner.Definition!.RawType.Data, null!, undo); break;
                case "owner-base-data": case "element-base-data":
                    var rawBase = (mutation.StartsWith("owner", StringComparison.Ordinal) ? owner : element).Definition!.RawBaseType!;
                    Replace(value => rawBase.Data = value, rawBase.Data, null!, undo);
                    Assert.That(X64ReferenceArrayScalarResetProof.Find(method), Is.Null);
                    break;
                case "owner-base-modifiers": case "element-base-modifiers":
                    var modifiedBase = (mutation.StartsWith("owner", StringComparison.Ordinal) ? owner : element).Definition!.RawBaseType!;
                    Replace(value => modifiedBase.NumMods = value, modifiedBase.NumMods, 1U, undo);
                    Assert.That(X64ReferenceArrayScalarResetProof.Find(method), Is.Null);
                    break;
                case "owner-base": Replace(value => owner.OverrideBaseType = value, owner.OverrideBaseType, element, undo); break;
                case "owner-size": ChangeInstanceSize(owner, undo); break;
                case "element-size": ChangeInstanceSize(element, undo); break;
                case "neighbor-overlap":
                    var neighbor = element.Fields.Single(field => field.Name == "Neighbor");
                    Replace(value => neighbor.OverrideOffset = value, neighbor.OverrideOffset, (int?)proof.ElementField.Offset, undo);
                    break;
                case "return-type": Replace(value => method.OverrideReturnType = value,
                    method.OverrideReturnType, app.SystemTypes.SystemInt32Type, undo); break;
                case "return-data": Replace(value => method.Definition!.RawReturnType!.Data = value,
                    method.Definition!.RawReturnType!.Data, null!, undo); break;
                case "method-synchronized": Replace(value => method.Definition!.iflags = value,
                    method.Definition!.iflags, (ushort)(method.Definition!.iflags | 0x0020), undo); break;
                case "field-token": Replace(value => proof.ElementField.BackingData!.Field.token = value,
                    proof.ElementField.BackingData!.Field.token, proof.ElementField.BackingData!.Field.token + 1, undo); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            Reject(method);
        }
        finally { for (var index = undo.Count - 1; index >= 0; index--) undo[index](); }
        Accept(method);
    }

    [TestCase("cache")]
    [TestCase("body")]
    [TestCase("null-helper")]
    [TestCase("bounds-helper")]
    public void CompleteCallerAndRuntimeHelpersRemainBoundToCurrentBytes(string mutation)
    {
        var method = Method("ResetValues");
        var proof = Accept(method);
        var cache = method.RawBytes;
        var pe = (PE)method.AppContext.Binary;
        var pointer = mutation switch
        {
            "null-helper" => proof.Native.NullCall.NearBranchTarget,
            "bounds-helper" => proof.Native.BoundsCall.NearBranchTarget,
            _ => method.UnderlyingPointer
        };
        var offset = checked((int)pe.MapVirtualAddressToRaw(pointer, false));
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

    [TestCase("field")]
    [TestCase("method")]
    [TestCase("cctor")]
    [TestCase("duplicate-binding")]
    public void OriginalOwnedMembersAndUniqueMethodBindingRemainRequired(string mutation)
    {
        var method = Method("ResetValues");
        Accept(method);
        var owner = method.DeclaringType!;
        var fields = owner.Fields.ToArray();
        var methods = owner.Methods.ToArray();
        var bindings = method.AppContext.MethodsByAddress[method.UnderlyingPointer];
        try
        {
            if (mutation == "field") owner.Fields.RemoveAt(0);
            else if (mutation == "method") owner.Methods.RemoveAt(0);
            else if (mutation == "duplicate-binding") bindings.Add(method);
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
            if (mutation == "duplicate-binding") bindings.RemoveAt(bindings.Count - 1);
        }
        Accept(method);
    }

    [TestCase(TypeAttributes.NestedPrivate, false, false)]
    [TestCase(TypeAttributes.NestedFamily, false, false)]
    [TestCase(TypeAttributes.NestedFamANDAssem, false, false)]
    [TestCase(TypeAttributes.NestedPublic, false, true)]
    [TestCase(TypeAttributes.NestedAssembly, false, true)]
    [TestCase(TypeAttributes.NestedFamORAssem, false, true)]
    [TestCase(TypeAttributes.NestedPrivate, true, true)]
    [TestCase(TypeAttributes.NestedFamily, true, true)]
    [TestCase(TypeAttributes.NestedFamANDAssem, true, true)]
    public void AFieldDoesNotGrantAccessToItsInaccessibleElementOwner(
        TypeAttributes visibility, bool withinCaller, bool accessible)
    {
        var method = Method("ResetActive");
        var proof = Accept(method);
        var owner = method.DeclaringType!;
        var element = proof.ElementField.DeclaringType;
        var parent = withinCaller ? owner : owner.DeclaringAssembly.Types.Single(type => type.Name == "SingleEntry");
        var flags = element.Definition!.Flags;
        var declaring = element.DeclaringType;
        var rawDeclaring = element.Definition.DeclaringTypeIndex;
        try
        {
            // Keep the metadata relation, declaring assembly and context membership
            // consistent so an inaccessible owner is the actual rejection reason.
            element.Definition.Flags = (uint)((element.Attributes & ~TypeAttributes.VisibilityMask) | visibility);
            element.Definition.DeclaringTypeIndex = parent.Definition!.ByvalTypeIndex;
            element.DeclaringType = parent;
            parent.NestedTypes.Add(element);
            Assert.That(X64ReferenceArrayScalarResetProof.AccessibleElement(owner, element), Is.EqualTo(accessible));
            Assert.That(X64ReferenceArrayScalarResetProof.Find(method) != null, Is.EqualTo(accessible));
        }
        finally
        {
            parent.NestedTypes.Remove(element);
            element.Definition.Flags = flags;
            element.Definition.DeclaringTypeIndex = rawDeclaring;
            element.DeclaringType = declaring;
        }
        Accept(method);
    }

    [Test]
    public void AVisibleNestedElementStillRequiresOriginalEnclosingMembership()
    {
        var method = Method("ResetActive");
        var proof = Accept(method);
        var owner = method.DeclaringType!;
        var element = proof.ElementField.DeclaringType;
        var middle = owner.DeclaringAssembly.Types.Single(type => type.Name == "SingleEntry");
        var elementFlags = element.Definition!.Flags;
        var elementParent = element.Definition.DeclaringTypeIndex;
        var originalElementParent = element.DeclaringType;
        try
        {
            element.Definition.Flags = (uint)((element.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NestedPublic);
            element.Definition.DeclaringTypeIndex = middle.Definition!.ByvalTypeIndex;
            element.DeclaringType = middle;
            Assert.That(X64ReferenceArrayScalarResetProof.AccessibleElement(owner, element), Is.False);
            Assert.That(X64ReferenceArrayScalarResetProof.Find(method), Is.Null);
        }
        finally
        {
            element.Definition.Flags = elementFlags;
            element.Definition.DeclaringTypeIndex = elementParent;
            element.DeclaringType = originalElementParent;
        }
        Accept(method);
    }

    [TestCase(TypeAttributes.NestedPublic, true)]
    [TestCase(TypeAttributes.NestedPrivate, false)]
    [TestCase(TypeAttributes.NestedFamily, false)]
    [TestCase(TypeAttributes.NestedAssembly, false)]
    [TestCase(TypeAttributes.NestedFamORAssem, false)]
    public void AVisibleTypeCannotBypassItsEnclosingOwnersVisibility(TypeAttributes visibility, bool accessible)
    {
        var owner = Method("ResetActive").DeclaringType!;
        var element = owner.AppContext.SystemTypes.SystemExceptionType;
        var middle = owner.AppContext.SystemTypes.SystemStringType;
        var outer = owner.AppContext.SystemTypes.SystemObjectType;
        var elementFlags = element.Definition!.Flags;
        var elementParent = element.Definition.DeclaringTypeIndex;
        var middleFlags = middle.Definition!.Flags;
        var middleParent = middle.Definition.DeclaringTypeIndex;
        var originalElementParent = element.DeclaringType;
        var originalMiddleParent = middle.DeclaringType;
        try
        {
            element.Definition.Flags = (uint)((element.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NestedPublic);
            element.Definition.DeclaringTypeIndex = middle.Definition.ByvalTypeIndex;
            element.DeclaringType = middle;
            middle.NestedTypes.Add(element);
            middle.Definition.Flags = (uint)((middle.Attributes & ~TypeAttributes.VisibilityMask) | visibility);
            middle.Definition.DeclaringTypeIndex = outer.Definition!.ByvalTypeIndex;
            middle.DeclaringType = outer;
            outer.NestedTypes.Add(middle);
            Assert.That(X64ReferenceArrayScalarResetProof.AccessibleElement(owner, element), Is.EqualTo(accessible));
        }
        finally
        {
            middle.NestedTypes.Remove(element);
            outer.NestedTypes.Remove(middle);
            element.Definition.Flags = elementFlags;
            element.Definition.DeclaringTypeIndex = elementParent;
            element.DeclaringType = originalElementParent;
            middle.Definition.Flags = middleFlags;
            middle.Definition.DeclaringTypeIndex = middleParent;
            middle.DeclaringType = originalMiddleParent;
        }
    }

    [TestCase("not-public")]
    [TestCase("missing-reference")]
    public void CrossAssemblyTypeAccessRequiresPublicIdentityAndAnOriginalDirectReference(string mutation)
    {
        var owner = Method("ResetActive").DeclaringType!;
        var target = owner.AppContext.SystemTypes.SystemExceptionType;
        Assert.That(X64ReferenceArrayScalarResetProof.AccessibleElement(owner, target), Is.True);
        var flags = target.Definition!.Flags;
        var count = owner.DeclaringAssembly.Definition!.ReferencedAssemblyCount;
        try
        {
            if (mutation == "not-public")
                target.Definition.Flags = (uint)((target.Attributes & ~TypeAttributes.VisibilityMask) | TypeAttributes.NotPublic);
            else owner.DeclaringAssembly.Definition.ReferencedAssemblyCount = 0;
            Assert.That(X64ReferenceArrayScalarResetProof.AccessibleElement(owner, target), Is.False);
        }
        finally
        {
            target.Definition.Flags = flags;
            owner.DeclaringAssembly.Definition.ReferencedAssemblyCount = count;
        }
        Assert.That(X64ReferenceArrayScalarResetProof.AccessibleElement(owner, target), Is.True);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void EvidenceCannotBeRemovedToPermitFallback(bool removeMark)
    {
        var method = Method("ResetActive");
        var proof = Accept(method);
        if (removeMark)
        {
            var table = (ConditionalWeakTable<MethodAnalysisContext, HashSet<string>>)
                typeof(NativeRecoveryProofTracker).GetField("Proofs", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Assert.That(table.TryGetValue(method, out var recorded), Is.True);
            Assert.That(recorded!.Remove(X64ReferenceArrayScalarResetProof.EvidenceKey), Is.True);
        }
        else method.PutExtraData<X64ReferenceArrayScalarResetProof.Proof>(X64ReferenceArrayScalarResetProof.EvidenceKey, null!);
        try { Reject(method); }
        finally
        {
            method.PutExtraData(X64ReferenceArrayScalarResetProof.EvidenceKey, proof);
            NativeRecoveryProofTracker.Mark(method, X64ReferenceArrayScalarResetProof.EvidenceKey);
        }
        Accept(method);
    }

    private MethodAnalysisContext Method(string name) => _methods.Single(method => method.Name == name);
    private static MethodDefinition Definition(MethodAnalysisContext method) => method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;

    private static X64ReferenceArrayScalarResetProof.Proof Accept(MethodAnalysisContext method)
    {
        Assert.That(X64ReferenceArrayScalarResetProof.TryAuthenticate(method, out var proof), Is.True, method.Name);
        Assert.That(X64ReferenceArrayScalarResetRecovery.TryGenerate(method, Definition(method)), Is.True);
        return proof;
    }

    private static void Reject(MethodAnalysisContext method)
    {
        Assert.That(X64ReferenceArrayScalarResetProof.TryAuthenticate(method, out _), Is.False);
        Assert.Throws<InvalidOperationException>(() => X64ReferenceArrayScalarResetRecovery.TryGenerate(method, Definition(method)));
    }

    private static void Replace<T>(Action<T> replace, T original, T changed, List<Action> undo)
    {
        undo.Add(() => replace(original));
        replace(changed);
    }

    private static void ChangeInstanceSize(TypeAnalysisContext type, List<Action> undo)
    {
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
