using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Analysis;

[NonParallelizable]
public class X64InertFloatConstructorProofTests
{
    [Test]
    public void ExactPlayerRetainsBitsAndRejectsChangedFloatEvidence()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_FLOAT_INITIALIZER_CTOR_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FLOAT_INITIALIZER_CTOR_FIXTURE_INPUT to the neutral exact player input.");

        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data",
            "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName(
                "FloatInitializerConstructorFixture")!.Types.Single(type =>
                type.Name == "FloatCells");
            var constructor = owner.Methods.Single(method =>
                method.Name == ".ctor");
            var baseConstructor = app.SystemTypes.SystemObjectType.Methods
                .Single(method => method.Name == ".ctor" &&
                    method.Parameters.Count == 0);
            constructor.EnsureRawBytes();
            var native = X86Utils.Iterate(constructor.RawBytes.AsSpan(),
                constructor.UnderlyingPointer, false);
            var expected = new (int Offset, uint Bits)[]
            {
                (16, 0xC1580000), (20, 0x80000000),
                (24, 0x7F800000), (28, 0xFF800000)
            };
            Assert.That(native.Take(6).Select(item => item.Code), Is.EqualTo(
                new[] { Code.Xor_r32_rm32, Code.Mov_rm32_imm32,
                    Code.Mov_rm32_imm32, Code.Mov_rm32_imm32,
                    Code.Mov_rm32_imm32, Code.Jmp_rel32_64 }));
            for (var index = 0; index < expected.Length; index++)
            {
                var store = native[index + 1];
                Assert.That(store.MemoryDisplacement64,
                    Is.EqualTo((ulong)expected[index].Offset));
                Assert.That(store.GetImmediate(1),
                    Is.EqualTo(expected[index].Bits));
            }

            constructor.Analyze();
            var stores = constructor.ControlFlowGraph!.Instructions
                .Where(instruction => instruction.OpCode == OpCode.Move &&
                    instruction.Operands is [FieldReference, FloatLiteral])
                .ToArray();
            Assert.That(stores.Length, Is.EqualTo(4));
            for (var index = 0; index < stores.Length; index++)
            {
                var field = (FieldReference)stores[index].Operands[0];
                var literal = (FloatLiteral)stores[index].Operands[1];
                Assert.That(field.Offset, Is.EqualTo(expected[index].Offset));
                Assert.That(unchecked((uint)BitConverter.SingleToInt32Bits(
                    literal.Value)), Is.EqualTo(expected[index].Bits));
            }
            var resolved = constructor.ControlFlowGraph.Instructions
                .Single(instruction => instruction.IsCall &&
                    instruction.NativeAddress == native[5].IP);
            Assert.That(resolved.Operands[0], Is.SameAs(baseConstructor));

            var target = native[5].NearBranchTarget;
            var aliases = app.MethodsByAddress[target];
            var convention = app.InstructionSet.CallingConventionResolver!;
            var receiver = constructor.ParameterLocals[0];
            var call = new Cpp2IL.Core.ISIL.Instruction(0,
                OpCode.CallVoid, new Immediate(unchecked((long)target)))
            {
                NativeAddress = native[5].IP,
                DeferredCallReturns = [],
                RawCallStackArgumentCount = 0
            };
            call.AddOperands(convention.ResolveForUnmanaged(app, target));
            call.SetOperand(1, receiver);
            call.SetOperand(2, new Immediate(0));
            Assert.That(convention.HasRawArgumentLayout(call, app), Is.True);
            Assert.That(X64InertObjectConstructorTailCallProof.Find(
                constructor, call, target, aliases), Is.SameAs(baseConstructor));

            var first = owner.Fields.Single(field => field.Name == "Finite");
            try
            {
                first.OverrideOffset = first.DefaultOffset + 1;
                Assert.That(X64InertObjectConstructorTailCallProof.Find(
                    constructor, call, target, aliases), Is.Null);
            }
            finally { first.OverrideOffset = null; }
            try
            {
                first.OverrideFieldType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64InertObjectConstructorTailCallProof.Find(
                    constructor, call, target, aliases), Is.Null);
            }
            finally { first.OverrideFieldType = null; }

            var bitsProof = typeof(X64InertObjectConstructorTailCallProof)
                .GetMethod("IsOwnSingleLiteralStore",
                    BindingFlags.Static | BindingFlags.NonPublic)!;
            foreach (var rejectedBits in new[] { 0x7FC01234u, 0x00000001u })
            {
                var source = native[1];
                var encoded = constructor.RawBytes.AsSpan().Slice(
                    checked((int)(source.IP - constructor.UnderlyingPointer)),
                    source.Length).ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(
                    encoded.AsSpan(encoded.Length - 4), rejectedBits);
                var altered = Decoder.Create(64,
                    new ByteArrayCodeReader(encoded), source.IP).Decode();
                Assert.That(bitsProof.Invoke(null, [altered, owner, receiver]),
                    Is.EqualTo(false),
                    "NaN and subnormal native stores require a separate source proof");
            }

            call.NativeAddress = native[4].IP;
            Assert.That(X64InertObjectConstructorTailCallProof.Find(
                constructor, call, target, aliases), Is.Null,
                "the complete native tail must remain at the callsite");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
