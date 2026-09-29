using System;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Analysis;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X86CctorBooleanFieldGetterProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerClosesOverlongObjectConstructorThunkWithSeparateCctor()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_CCTOR_BOOLEAN_GETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CCTOR_BOOLEAN_GETTER_FIXTURE_INPUT to the synthetic player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("CctorBooleanGetterFixture")!.Types
                .Single(type => type.Name == "FlagState");
            var constructor = owner.Methods.Single(method => method.Name == ".ctor");
            constructor.EnsureRawBytes();
            var native = X86Utils.Iterate(constructor).ToArray();
            var thunk = native.Take(2).ToArray();
            var target = thunk[1].NearBranchTarget;
            var objectConstructor = app.SystemTypes.SystemObjectType.Methods.Single(method =>
                method.Name == ".ctor" && method.Parameters.Count == 0 &&
                method.UnderlyingPointer == target);
            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;
            var start = constructor.UnderlyingPointer;
            var end = thunk[1].NextIP;
            var neighbor = native.First(instruction => instruction.IP >= start + 16);
            Assert.Multiple(() =>
            {
                Assert.That(owner.Definition!.HasCctor, Is.True);
                Assert.That(constructor.RawBytes.Length, Is.GreaterThan(7));
                Assert.That(end - start, Is.EqualTo(7));
                Assert.That(ConstructorChainRecovery.TryProveShape(thunk, start, 7, target), Is.True);
                Assert.That(X64IteratorFactoryProof.ProveInertObjectConstructor(app, target, pe, unwind), Is.True);
                Assert.That(unwind.ClassifySpan(start, end).Kind, Is.EqualTo(X64UnwindProof.SpanKind.NoEntry));
                Assert.That(X64NativePaddingProof.HasInt3Padding(pe, end, neighbor.IP), Is.True);
                Assert.That(unwind.HasFunctionEntryAt(neighbor.IP, neighbor.NextIP), Is.True);
                Assert.That(X64ObjectConstructorThunkProof.Find(constructor, native),
                    Is.SameAs(objectConstructor));
            });

            var cctor = owner.Methods.Single(method => method.Name == ".cctor");
            try
            {
                cctor.OverrideName = "ChangedClassConstructor";
                Assert.That(X64ObjectConstructorThunkProof.Find(constructor, native), Is.Null,
                    "Constructor admission requires consistent cctor metadata");
            }
            finally { cctor.OverrideName = null; }

            var changedNative = native.ToArray();
            var decoder = Decoder.Create(64, new ByteArrayCodeReader([0x90]), native[2].IP);
            changedNative[2] = decoder.Decode();
            Assert.That(X64ObjectConstructorThunkProof.Find(constructor, changedNative), Is.Null,
                "An altered alignment suffix must not be admitted as a closed thunk");

            var savedBytes = constructor.RawBytes;
            try
            {
                var changedBytes = savedBytes.AsSpan().ToArray();
                changedBytes[0] ^= 1;
                constructor.RawBytes = new BinarySlice(changedBytes);
                Assert.That(X64ObjectConstructorThunkProof.Find(constructor, native), Is.Null,
                    "Cached native bytes must remain byte-exact to the player image");
            }
            finally { constructor.RawBytes = savedBytes; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerProvesInstanceGetterWithSeparateClassConstructor()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_CCTOR_BOOLEAN_GETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CCTOR_BOOLEAN_GETTER_FIXTURE_INPUT to the synthetic player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("CctorBooleanGetterFixture")!.Types
                .Single(type => type.Name == "FlagState");
            var getter = owner.Methods.Single(method => method.Name == "ReadFlag");
            var field = owner.Fields.Single(candidate => candidate.Name == "Flag");
            var neighbor = owner.Fields.Single(candidate => candidate.Name == "Neighbor");
            Assert.Multiple(() =>
            {
                Assert.That(owner.Definition!.HasCctor, Is.True);
                Assert.That(owner.Methods.Count(method => method.Name == ".cctor"), Is.EqualTo(1));
                Assert.That(getter.IsStatic, Is.False);
                Assert.That(getter.IsVirtual, Is.False);
                Assert.That(field.Offset, Is.Not.EqualTo(neighbor.Offset));
            });

            getter.EnsureRawBytes();
            var native = X86Utils.Iterate(getter).ToArray();
            Assert.That(X86DirectBooleanFieldGetterProof.TryProveShape(native), Is.Not.Null);
            Assert.That(X86DirectBooleanFieldGetterProof.Find(getter, native), Is.SameAs(field));
            var lifted = app.InstructionSet.GetIsilFromMethod(getter);
            Assert.That(lifted.Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { OpCode.Move, OpCode.Return }));
            Assert.That(lifted[0].IntegerBitWidth, Is.EqualTo(8));
            getter.Analyze();
            Assert.That(getter.AnalysisWarnings, Is.Empty);
            Assert.That(getter.ControlFlowGraph!.Instructions.Any(instruction =>
                instruction is { OpCode: OpCode.Move, Operands: [_, FieldReference resolved] } &&
                ReferenceEquals(resolved.Field, field)), Is.True);

            try
            {
                field.OverrideOffset = neighbor.Offset;
                Assert.That(X86DirectBooleanFieldGetterProof.Find(getter, native), Is.Null,
                    "Changed Boolean field placement must not inherit the original native proof");
            }
            finally { field.OverrideOffset = null; }

            var cctor = owner.Methods.Single(method => method.Name == ".cctor");
            try
            {
                cctor.OverrideName = "ChangedClassConstructor";
                Assert.That(X86DirectBooleanFieldGetterProof.Find(getter, native), Is.Null,
                    "Getter admission requires consistent cctor metadata");
            }
            finally { cctor.OverrideName = null; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
