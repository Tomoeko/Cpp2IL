using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional original-player binding and output-restoration checks.</summary>
[NonParallelizable]
public class X64CctorStaticFieldReadFixtureTests
{
    private MethodAnalysisContext[] _methods = null!;

    [OneTimeSetUp]
    public void LoadOriginalPlayer()
    {
        var binary = Environment.GetEnvironmentVariable("CPP2IL_CCTOR_READ_BINARY");
        var metadata = Environment.GetEnvironmentVariable("CPP2IL_CCTOR_READ_METADATA");
        if (string.IsNullOrEmpty(binary) || string.IsNullOrEmpty(metadata))
            Assert.Ignore("Set both cctor reference-read paths to an original exact-target player.");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(binary!, metadata!, UnityVersion.Parse("2021.3.35f1"));
        var app = Cpp2IlApi.CurrentAppContext!;
        _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
        _methods = app.Assemblies.SelectMany(assembly => assembly.Types)
            .Where(type => type.Definition?.HasCctor == true)
            .SelectMany(type => type.Methods).Where(method => method.Parameters.Count == 0 &&
                X64CctorStaticFieldReadProof.Find(method) != null).ToArray();
        Assert.That(_methods, Is.Not.Empty, "The supplied player must contain this complete caller recipe.");
    }

    [OneTimeTearDown]
    public void ReleasePlayer() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void TypedReadsPreserveStaticOrVirtualInstanceDeclarationsAndStayPartial()
    {
        foreach (var method in _methods)
        {
            var proof = X64CctorStaticFieldReadProof.Find(method)!;
            var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            Assert.That(X64CctorStaticFieldReadRecovery.TryGeneratePartial(method, output, out var reasons), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(reasons, Has.Length.EqualTo(3));
                Assert.That(reasons.Any(reason => reason.Contains("cached initialization exceptions", StringComparison.Ordinal)), Is.True);
                Assert.That(output.Signature!.HasThis, Is.EqualTo(!method.IsStatic));
                Assert.That((ushort)output.Attributes, Is.EqualTo((ushort)method.Attributes));
                Assert.That(output.CilMethodBody!.Instructions.Select(instruction => instruction.OpCode),
                    Is.EqualTo(new[] { CilOpCodes.Ldsfld, CilOpCodes.Ret }));
                Assert.That(output.CilMethodBody.Instructions[0].Operand,
                    Is.SameAs(proof.Field.GetExtraData<IFieldDescriptor>("AsmResolverField")));
            });
        }
    }

    [TestCase("class-flags")]
    [TestCase("method-flags")]
    [TestCase("constructor-identity")]
    [TestCase("field-offset")]
    [TestCase("field-identity")]
    [TestCase("size-pointer-cache")]
    [TestCase("duplicate-alias")]
    [TestCase("ineligible-alias")]
    public void OriginalRowsLayoutsConstructorAndAllFoldedIdentitiesRemainMandatory(string defect)
    {
        foreach (var method in _methods)
        {
            var proof = X64CctorStaticFieldReadProof.Find(method)!;
            var owner = method.DeclaringType!;
            var undo = new Stack<Action>();
            void Assign<T>(Func<T> read, Action<T> write, T changed)
            {
                var original = read(); undo.Push(() => write(original)); write(changed);
            }
            try
            {
                switch (defect)
                {
                    case "class-flags": Assign(() => owner.Attributes, value => owner.Attributes = value,
                        owner.Attributes ^ TypeAttributes.BeforeFieldInit); break;
                    case "method-flags": Assign(() => method.Definition!.flags, value => method.Definition!.flags = value,
                        (ushort)(method.Definition!.flags ^ (ushort)MethodAttributes.Static)); break;
                    case "constructor-identity": Assign(() => proof.Constructor.Name, value => proof.Constructor.Name = value, "Changed"); break;
                    case "field-offset": Assign(() => proof.Field.Offset, value => proof.Field.Offset = value, proof.Field.Offset + 8); break;
                    case "field-identity": Assign(() => proof.Field.BackingData!.Field.nameIndex,
                        value => proof.Field.BackingData!.Field.nameIndex = value, proof.Field.BackingData!.Field.nameIndex + 1); break;
                    case "size-pointer-cache":
                        var pointers = ((PE)method.AppContext.Binary).TypeDefinitionSizePointers;
                        var ordinal = owner.Definition!.TypeIndex.Value;
                        Assign(() => pointers[ordinal], value => pointers[ordinal] = value, checked(pointers[ordinal] + 4)); break;
                    case "duplicate-alias":
                        method.AppContext.MethodsByAddress[method.UnderlyingPointer].Add(method);
                        undo.Push(() => method.AppContext.MethodsByAddress[method.UnderlyingPointer].RemoveAt(
                            method.AppContext.MethodsByAddress[method.UnderlyingPointer].Count - 1)); break;
                    case "ineligible-alias":
                        method.AppContext.MethodsByAddress[method.UnderlyingPointer].Add(proof.Constructor);
                        undo.Push(() => method.AppContext.MethodsByAddress[method.UnderlyingPointer].Remove(proof.Constructor)); break;
                }
                Assert.That(X64CctorStaticFieldReadProof.Find(method), Is.Null, defect);
            }
            finally { while (undo.TryPop(out var restore)) restore(); }
            Assert.That(proof.Matches(X64CctorStaticFieldReadProof.Find(method)!), Is.True,
                "Restoration must recover the same original proof, including native input bytes.");
        }
    }

    [TestCase("field")]
    [TestCase("constructor")]
    [TestCase("signature")]
    public void ChangedOutputIdentitiesPreserveThePreviousBody(string defect)
    {
        foreach (var method in _methods)
        {
            var proof = X64CctorStaticFieldReadProof.Find(method)!;
            var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            var field = proof.Field.GetExtraData<FieldDefinition>("AsmResolverField")!;
            var constructor = proof.Constructor.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            var owner = field.DeclaringType!;
            var replacement = new FieldDefinition(field.Name, field.Attributes, field.Signature);
            var savedName = constructor.Name;
            var savedHasThis = output.Signature!.HasThis;
            var savedBody = output.CilMethodBody;
            var sentinel = new CilMethodBody();
            output.CilMethodBody = sentinel;
            try
            {
                if (defect == "field")
                {
                    owner.Fields.Add(replacement);
                    proof.Field.PutExtraData("AsmResolverField", replacement);
                }
                else if (defect == "constructor") constructor.Name = "Changed";
                else output.Signature.HasThis = !savedHasThis;
                Assert.That(X64CctorStaticFieldReadRecovery.TryGeneratePartial(method, output, out var reasons), Is.False);
                Assert.That(reasons, Is.Empty);
                Assert.That(output.CilMethodBody, Is.SameAs(sentinel));
            }
            finally
            {
                proof.Field.PutExtraData("AsmResolverField", field);
                owner.Fields.Remove(replacement);
                constructor.Name = savedName;
                output.Signature.HasThis = savedHasThis;
                output.CilMethodBody = savedBody;
            }
        }
    }
}
