using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ConditionalManagedThrowProofTests
{
    [Test]
    public void ClosedShapeRejectsAlteredWidthBranchAndEffects()
    {
        var body = SyntheticBody();
        var shape = X64ConditionalManagedThrowProof.TryProveShape(body);
        Assert.That(shape, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(shape!.MetadataInitializer, Is.EqualTo(0x4000UL));
            Assert.That(shape.Allocator, Is.EqualTo(0x5000UL));
            Assert.That(shape.NullGuard, Is.EqualTo(0x6000UL));
            Assert.That(shape.Constructor, Is.EqualTo(0x7000UL));
            Assert.That(shape.Raiser, Is.EqualTo(0x8000UL));
            Assert.That(X64ConditionalManagedThrowProof.TryProveShape(body[..22]),
                Is.Null, "the unreachable nonvolatile restore remains part of the authenticated body");
        });

        var mutations = new (int Index, Action<Instruction[]> Change)[]
        {
            (1, native => native[1].Op0Register = Register.ECX),
            (2, native => native[2].NearBranch64 = native[17].IP),
            (2, native => native[2].Code = Code.Je_rel8_64),
            (3, native => native[3].MemoryDisplacement64 = 2),
            (3, native => native[3].Op0Register = Register.RAX),
            (7, native => native[7].MemoryDisplacement64 = 0x18),
            (12, native => native[12].Op1Register = Register.RDX),
            (14, native => native[14].Code = Code.Inc_rm32),
            (18, native => native[18].NearBranch64 = 0x9000),
            (19, native => native[19].Code = Code.Mov_r32_rm32),
            (20, native => native[20].Op1Register = Register.RAX),
            (21, native => native[21].Code = Code.Jmp_rel32_64),
            (22, native => native[22].MemoryDisplacement64 = 0x18),
        };
        foreach (var (index, change) in mutations)
        {
            var altered = body.ToArray();
            change(altered);
            Assert.That(X64ConditionalManagedThrowProof.TryProveShape(altered),
                Is.Null, $"instruction {index} changes the proved width, edge or effect");
        }
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerProvesBothExitsAndRejectsUnboundIdentity()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_CONDITIONAL_MANAGED_THROW_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_CONDITIONAL_MANAGED_THROW_FIXTURE_INPUT to the neutral player-input directory.");

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
            var owner = app.GetAssemblyByName("ConditionalManagedThrowFixture")!.Types
                .Single(type => type.Name == "ConditionalManagedThrow");
            var method = owner.Methods.Single(candidate => candidate.Name == "ReturnOrThrow");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            var shape = X64ConditionalManagedThrowProof.TryProveShape(native.Take(23).ToArray());
            var evidence = X64ConditionalManagedThrowProof.Find(method, native);
            Assert.That(shape, Is.Not.Null);
            Assert.That(evidence, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(evidence!.ExceptionType.FullName,
                    Is.EqualTo("System.NotSupportedException"));
                Assert.That(evidence.Constructor.Name, Is.EqualTo(".ctor"));
                Assert.That(X64ConditionalManagedThrowProof.Find(method, native.Take(22).ToArray()),
                    Is.Null, "truncating the independently authenticated unwind region is rejected");
            });

            var lifted = X64ConditionalManagedThrowProof.TryLift(method, native)!;
            Assert.Multiple(() =>
            {
                Assert.That(lifted.Select(instruction => instruction.OpCode),
                    Is.EqualTo(new[] { ISIL.OpCode.CheckNotEqual, ISIL.OpCode.ConditionalJump,
                        ISIL.OpCode.Add, ISIL.OpCode.Return, ISIL.OpCode.Newobj,
                        ISIL.OpCode.CallVoid, ISIL.OpCode.Throw }));
                Assert.That(lifted[1].Operands[0], Is.SameAs(lifted[4]));
                Assert.That(lifted[2].IntegerBitWidth, Is.EqualTo(32));
                Assert.That(lifted[4].Operands[1], Is.SameAs(evidence!.ExceptionType));
                Assert.That(lifted[5].Operands[0], Is.SameAs(evidence.Constructor));
                Assert.That(lifted[4].Destination, Is.EqualTo(lifted[5].Operands[1]));
                Assert.That(lifted[5].Operands[1], Is.EqualTo(lifted[6].Operands[0]));
            });

            foreach (var altered in new[]
            {
                shape! with { MethodDefSlot = shape.TypeInfoSlot },
                shape with { TypeInfoSlot = shape.MethodDefSlot },
                shape with { MetadataInitializer = shape.Allocator },
                shape with { Allocator = shape.Constructor },
                shape with { NullGuard = shape.Allocator },
                shape with { Constructor = shape.MetadataInitializer },
                shape with { Raiser = shape.Constructor },
            })
                Assert.That(X64ConditionalManagedThrowProof.BindProvedShape(method, altered),
                    Is.Null, "the constructor, slots and runtime helper identities are independently bound");

            var changedNative = native.ToArray();
            changedNative[21].NearBranch64 = shape!.Constructor;
            Assert.That(X64ConditionalManagedThrowProof.Find(method, changedNative), Is.Null,
                "a returning native call cannot replace the authenticated raise target");

            var originalBytes = method.RawBytes;
            try
            {
                var changedBytes = originalBytes.AsSpan().ToArray();
                changedBytes[0] ^= 1;
                method.RawBytes = new BinarySlice(changedBytes);
                Assert.That(X64ConditionalManagedThrowProof.Find(method, native), Is.Null,
                    "a nonempty cached prefix must remain consistent with the player image");
                Assert.That(method.RawBytes.AsSpan().ToArray(), Is.EqualTo(changedBytes),
                    "revalidation cannot silently refresh away conflicting native evidence");
            }
            finally { method.RawBytes = originalBytes; }

            try
            {
                method.Parameters[0].ParameterType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64ConditionalManagedThrowProof.Find(method, native), Is.Null,
                    "the tested low byte must remain the original Boolean parameter");
            }
            finally { method.Parameters[0].OverrideParameterType = null; }
            try
            {
                method.Parameters[1].Attributes |= ParameterAttributes.Out;
                Assert.That(X64ConditionalManagedThrowProof.Find(method, native), Is.Null);
            }
            finally { method.Parameters[1].OverrideAttributes = null; }
            try
            {
                method.ReturnType = app.SystemTypes.SystemUInt32Type;
                Assert.That(X64ConditionalManagedThrowProof.Find(method, native), Is.Null);
            }
            finally { method.OverrideReturnType = null; }

            var constructor = evidence!.Constructor;
            var constructorDefinition = constructor.Definition!;
            var originalFlags = constructorDefinition.flags;
            foreach (var invalid in new[] { MethodAttributes.Abstract, MethodAttributes.PinvokeImpl })
            {
                try
                {
                    constructorDefinition.flags = (ushort)(originalFlags | (ushort)invalid);
                    Assert.That(constructor.Attributes, Is.EqualTo(constructor.DefaultAttributes));
                    Assert.That(X64ConditionalManagedThrowProof.BindProvedShape(method, shape),
                        Is.Null, "invalid original constructor flags cannot identify managed construction");
                }
                finally { constructorDefinition.flags = originalFlags; }
            }

            foreach (var definition in new[] { method.Definition!, constructorDefinition })
            {
                var originalImplementation = definition.iflags;
                try
                {
                    definition.iflags |= 0xF000;
                    Assert.That(definition.IsUnmanagedCallersOnly, Is.True);
                    Assert.That(X64ConditionalManagedThrowProof.BindProvedShape(method, shape),
                        Is.Null, "a complete extension marker cannot authorize the ordinary managed caller or constructor ABI");
                    Assert.That(X64ConditionalManagedThrowProof.Find(method, native), Is.Null);
                }
                finally { definition.iflags = originalImplementation; }
            }
            Assert.That(X64ConditionalManagedThrowProof.Find(method, native), Is.Not.Null);

            var bindings = app.MethodsByAddress[constructor.UnderlyingPointer];
            bindings.Add(method);
            try
            {
                Assert.That(X64ConditionalManagedThrowProof.Find(method, native), Is.Null,
                    "an aliased constructor does not uniquely identify the managed allocation");
            }
            finally { bindings.Remove(method); }

            var callerBindings = app.MethodsByAddress[method.UnderlyingPointer];
            callerBindings.Add(constructor);
            try
            {
                Assert.That(X64ConditionalManagedThrowProof.Find(method, native), Is.Null,
                    "a shared caller address must retain an unresolved disposition");
            }
            finally { callerBindings.Remove(constructor); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static Instruction[] SyntheticBody()
    {
        // Neutral register/frame operations with artificial call targets. The
        // exact-player check separately authenticates bytes, unwind and metadata.
        byte[] bytes =
        [
            0x48, 0x83, 0xEC, 0x28, 0x84, 0xC9, 0x75, 0x08,
            0x8D, 0x42, 0x01, 0x48, 0x83, 0xC4, 0x28, 0xC3,
            0x48, 0x8D, 0x0D, 0, 0, 0, 0,
            0x48, 0x89, 0x5C, 0x24, 0x20,
            0xE8, 0, 0, 0, 0, 0x48, 0x8B, 0xC8,
            0xE8, 0, 0, 0, 0, 0x48, 0x8B, 0xC8, 0x48, 0x8B, 0xD8,
            0xE8, 0, 0, 0, 0, 0x33, 0xD2, 0x48, 0x8B, 0xCB,
            0xE8, 0, 0, 0, 0, 0x48, 0x8D, 0x0D, 0, 0, 0, 0,
            0xE8, 0, 0, 0, 0, 0x48, 0x8B, 0xD0, 0x48, 0x8B, 0xCB,
            0xE8, 0, 0, 0, 0, 0x48, 0x8B, 0x5C, 0x24, 0x20,
        ];
        var native = X86Utils.Iterate(bytes, 0x1000, is32Bit: false).ToArray();
        native[8].NearBranch64 = 0x4000;
        native[10].NearBranch64 = 0x5000;
        native[13].NearBranch64 = 0x6000;
        native[16].NearBranch64 = 0x7000;
        native[18].NearBranch64 = 0x4000;
        native[21].NearBranch64 = 0x8000;
        return native;
    }
}
