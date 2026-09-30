using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.Tests.Isil;

public class X86ScalarZeroReturnFixtureTests
{
    [Test]
    [NonParallelizable]
    public void ExactTargetScalarZeroLeavesHaveClosedNativeBodies()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_SCALAR_ZERO_RETURN_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_SCALAR_ZERO_RETURN_FIXTURE_INPUT to the neutral player input.");

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(
                Path.Combine(directory, "GameAssembly.dll"),
                Path.Combine(directory, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
                    "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var owner = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("ScalarZeroReturnFixture")!.Types
                .Single(type => type.Name == "ScalarZeroReturns");
            var pointers = new System.Collections.Generic.List<ulong>();
            foreach (var name in new[] { "SingleZero", "DoubleZero" })
            {
                var method = owner.Methods.Single(candidate => candidate.Name == name);
                method.EnsureRawBytes();
                var native = X86Utils.Iterate(method).ToArray();
                pointers.Add(method.UnderlyingPointer);
                Assert.That(method.RawBytes.AsSpan().ToArray(),
                    Is.EqualTo(new byte[] { 0x0F, 0x57, 0xC0, 0xC3 }));
                Assert.That(native.Length, Is.EqualTo(2));
                Assert.That(native[0].Mnemonic, Is.EqualTo(Mnemonic.Xorps));
                Assert.That(native[0].Op0Register, Is.EqualTo(NativeRegister.XMM0));
                Assert.That(native[0].Op1Register, Is.EqualTo(NativeRegister.XMM0));
                Assert.That(native[1].Code, Is.EqualTo(Code.Retnq));
                var lifted = X64ScalarZeroReturnProof.TryLift(method, native);
                Assert.That(lifted, Is.Not.Null);
                Assert.That(lifted!, Has.Count.EqualTo(1));
                Assert.That(lifted[0].OpCode, Is.EqualTo(OpCode.Return));
                Assert.That(lifted[0].Operands[0], name == "SingleZero"
                    ? Is.EqualTo(new FloatLiteral(0f))
                    : Is.EqualTo(new DoubleLiteral(0d)));
                Assert.That(lifted[0].Operands[0] switch
                {
                    FloatLiteral value => BitConverter.SingleToUInt32Bits(value.Value),
                    DoubleLiteral value => BitConverter.DoubleToUInt64Bits(value.Value),
                    _ => ulong.MaxValue,
                }, Is.Zero, "the evidenced result is positive zero, including its sign bit");

                try
                {
                    method.OverrideReturnType = name == "SingleZero"
                        ? method.AppContext.SystemTypes.SystemDoubleType
                        : method.AppContext.SystemTypes.SystemSingleType;
                    Assert.That(X64ScalarZeroReturnProof.TryLift(method, native), Is.Null,
                        "even two compatible native return lanes cannot replace the declared managed signature");
                }
                finally { method.OverrideReturnType = null; }

                var originalBytes = method.RawBytes;
                try
                {
                    method.RawBytes = new BinarySlice(Convert.FromHexString("0F57C1C3"));
                    Assert.That(X64ScalarZeroReturnProof.TryLift(method, native), Is.Null,
                        "decoded instructions must still agree with the complete player bytes");
                }
                finally { method.RawBytes = originalBytes; }

                var interior = method.UnderlyingPointer + 1;
                Assert.That(method.AppContext.MethodsByAddress.ContainsKey(interior), Is.False);
                try
                {
                    method.AppContext.MethodsByAddress[interior] = [method];
                    Assert.That(X64ScalarZeroReturnProof.TryLift(method, native), Is.Null,
                        "a second managed entry inside the leaf invalidates its closed boundary");
                }
                finally { method.AppContext.MethodsByAddress.Remove(interior); }

                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty);
                Assert.That(method.Locals, Is.Empty);
                Assert.That(method.ParameterLocals, Is.Empty);
                var analyzedReturn = method.ControlFlowGraph!.Instructions.Single();
                Assert.That(analyzedReturn.OpCode, Is.EqualTo(OpCode.Return));
                Assert.That(analyzedReturn.Operands[0], Is.EqualTo(lifted[0].Operands[0]),
                    "full analysis must preserve the proved scalar zero without inventing a local");
            }
            Assert.That(pointers[0], Is.EqualTo(pointers[1]),
                "this control requires two metadata signatures sharing one native body");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerProvesStaticAndInstanceLeavesWithIndependentManagedIdentity()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_SCALAR_POSITIVE_ZERO_LEAF_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_SCALAR_POSITIVE_ZERO_LEAF_FIXTURE_INPUT to the neutral player input.");

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(
                Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
                    "global-metadata.dat"), UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName("ScalarPositiveZeroLeafFixture")!;
            var leaves = assembly.Types.SelectMany(type => type.Methods)
                .Where(method => method.Name is "SingleZero" or "DoubleZero").ToArray();
            Assert.That(leaves.Length, Is.EqualTo(4));
            Assert.That(leaves.Count(method => method.IsStatic), Is.EqualTo(2));
            foreach (var method in leaves)
            {
                method.EnsureRawBytes();
                var native = X86Utils.Iterate(method).ToArray();
                Assert.That(X64ScalarZeroReturnProof.MatchesBody(native, method.UnderlyingPointer), Is.True);
                var lifted = X64ScalarZeroReturnProof.TryLift(method, native);
                Assert.That(lifted, Is.Not.Null, "each folded managed identity is independently proved");
                Assert.That(lifted!, Has.Count.EqualTo(1));
                Assert.That(lifted[0].NativeAddress, Is.EqualTo(native[^1].IP));
                Assert.That(lifted[0].OpCode, Is.EqualTo(OpCode.Return));
                Assert.That(lifted[0].Operands[0] switch
                {
                    FloatLiteral value => BitConverter.SingleToUInt32Bits(value.Value),
                    DoubleLiteral value => BitConverter.DoubleToUInt64Bits(value.Value),
                    _ => ulong.MaxValue,
                }, Is.Zero, "the sign bit is part of the recovered result");
                Assert.That(lifted[0].Operands[0], method.Name == "SingleZero"
                    ? Is.TypeOf<FloatLiteral>() : Is.TypeOf<DoubleLiteral>());

                var bytes = method.RawBytes;
                try
                {
                    var changed = bytes.AsSpan().ToArray();
                    changed[checked((int)(native[^1].NextIP - method.UnderlyingPointer)) - 1] ^= 1;
                    method.RawBytes = new BinarySlice(changed);
                    Assert.That(X64ScalarZeroReturnProof.TryLift(method, native), Is.Null);
                    Assert.That(method.RawBytes.AsSpan().ToArray(), Is.EqualTo(changed),
                        "the proof cannot refresh away a conflicting cached body");
                }
                finally { method.RawBytes = bytes; }

                try
                {
                    method.ReturnType = method.Name == "SingleZero"
                        ? app.SystemTypes.SystemDoubleType : app.SystemTypes.SystemSingleType;
                    Assert.That(X64ScalarZeroReturnProof.TryLift(method, native), Is.Null,
                        "equal native zero bits cannot replace the original managed return type");
                }
                finally { method.OverrideReturnType = null; }

                try
                {
                    method.Attributes ^= MethodAttributes.Static;
                    Assert.That(X64ScalarZeroReturnProof.TryLift(method, native), Is.Null,
                        "the ordinary managed receiver/MethodInfo argument slots must remain unchanged");
                }
                finally { method.OverrideAttributes = null; }

                var definition = method.Definition!;
                var implementation = definition.iflags;
                foreach (var invalid in new ushort[]
                         { (ushort)MethodImplAttributes.Synchronized, 0x1000, 0xF000 })
                {
                    try
                    {
                        definition.iflags = (ushort)(implementation | invalid);
                        Assert.That(X64ScalarZeroReturnProof.TryLift(method, native), Is.Null,
                            "synchronized, runtime or extension ABI metadata cannot authorize an ordinary managed leaf");
                    }
                    finally { definition.iflags = implementation; }
                }
                var flags = definition.flags;
                try
                {
                    definition.flags |= (ushort)MethodAttributes.Virtual;
                    Assert.That(X64ScalarZeroReturnProof.TryLift(method, native), Is.Null,
                        "virtual dispatch is outside this ordinary instance leaf proof");
                }
                finally { definition.flags = flags; }

                var bindings = app.MethodsByAddress[method.UnderlyingPointer];
                bindings.Add(method);
                try
                {
                    Assert.That(X64ScalarZeroReturnProof.TryLift(method, native), Is.Null,
                        "duplicate descriptors do not supply independent folded managed owners");
                }
                finally { bindings.RemoveAt(bindings.Count - 1); }

                foreach (var interior in new[] { method.UnderlyingPointer + 1, native[^1].NextIP })
                {
                    Assert.That(app.MethodsByAddress.ContainsKey(interior), Is.False);
                    app.MethodsByAddress[interior] = [method];
                    try
                    {
                        Assert.That(X64ScalarZeroReturnProof.TryLift(method, native), Is.Null,
                            "a managed entry inside the body or terminal trap padding is not a closed leaf");
                    }
                    finally { app.MethodsByAddress.Remove(interior); }
                }

                method.Analyze();
                Assert.That(method.AnalysisWarnings, Is.Empty);
                var result = method.ControlFlowGraph!.Instructions.Single();
                Assert.That(result.OpCode, Is.EqualTo(OpCode.Return));
                Assert.That(result.Operands[0], Is.EqualTo(lifted[0].Operands[0]));
            }
            Assert.That(leaves.Select(method => method.UnderlyingPointer).Distinct().Count(),
                Is.LessThan(leaves.Length), "the neutral control must exercise distinct folded metadata owners");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [TestCase("0F57C9C3")] // clears XMM1, not the return register
    [TestCase("0F57C1C3")] // XMM0 XOR XMM1 depends on an incoming value
    [TestCase("660F57C0C3")] // XORPD has a distinct encoding
    [TestCase("0F57C0CC")] // missing return
    [TestCase("0F57C0C20800")] // stack-adjusting return
    [TestCase("F30F57C0C3")] // an unproved instruction prefix
    public void OtherPackedBodiesDoNotProveAScalarReturn(string bytes)
    {
        var decoder = Decoder.Create(64,
            new ByteArrayCodeReader(Convert.FromHexString(bytes)));
        var native = new[] { decoder.Decode(), decoder.Decode() };
        Assert.That(X64ScalarZeroReturnProof.MatchesBody(native, 0), Is.False);
    }

    [Test]
    public void ClosedPackedClearRequiresContiguousAddressesAndTheEntireDecodedBody()
    {
        const ulong start = 0x1000;
        var decoder = Decoder.Create(64,
            new ByteArrayCodeReader(Convert.FromHexString("0F57C0C390")), start);
        var clear = decoder.Decode();
        var ret = decoder.Decode();
        var trailing = decoder.Decode();
        Assert.That(X64ScalarZeroReturnProof.MatchesBody([clear, ret], start), Is.True);
        Assert.That(X64ScalarZeroReturnProof.MatchesBody([clear, ret], start + 1), Is.False);
        Assert.That(X64ScalarZeroReturnProof.MatchesBody([clear, ret, trailing], start), Is.False);
        ret.IP++;
        Assert.That(X64ScalarZeroReturnProof.MatchesBody([clear, ret], start), Is.False);
    }

    [TestCase("0F57C0C3")]
    [TestCase("66900F57C0C3")]
    public void OnlyTheEvidencedNeutralNopPrefixCanPrecedeThePositiveZeroLeaf(string bytes)
    {
        Assert.That(X64ScalarZeroReturnProof.MatchesBody(Decode(bytes), 0x1000), Is.True);
    }

    [TestCase("900F57C0C3")] // the supported prefix has exactly the evidenced two-byte encoding
    [TestCase("6666900F57C0C3")] // redundant operand-size prefixes are outside the proved body
    [TestCase("669066900F57C0C3")] // two NOPs are not one authenticated prefix
    [TestCase("0F1F000F57C0C3")] // memory-form NOP is a different native body
    [TestCase("F3900F57C0C3")] // PAUSE is not the neutral prefix
    [TestCase("66900F57C0F3C3")] // an unproved return prefix
    [TestCase("B800000080660F6EC0C3")] // negative Single zero is different even though numeric equality holds
    [TestCase("48B8000000000000008066480F6EC0C3")] // negative Double zero
    [TestCase("4885C974030F57C0C3")] // a receiver-dependent branch cannot be discarded
    [TestCase("E8000000000F57C0C3")] // an observable call before the clear
    [TestCase("66900F57C0C3CC")] // padding is authenticated separately and is not a decoded body instruction
    public void SignedZeroAndUnprovedPrefixesEffectsOrControlRemainUnrecovered(string bytes)
    {
        Assert.That(X64ScalarZeroReturnProof.MatchesBody(Decode(bytes), 0x1000), Is.False);
    }

    [Test]
    public void PrefixMustBeContiguousAndDecodedForThe64BitTarget()
    {
        var native = Decode("66900F57C0C3");
        native[0].IP++;
        Assert.That(X64ScalarZeroReturnProof.MatchesBody(native, 0x1000), Is.False);
        Assert.That(X64ScalarZeroReturnProof.MatchesBody(Decode("66900F57C0C3", 32), 0x1000), Is.False);
    }

    private static Iced.Intel.Instruction[] Decode(string hex, int bitness = 64)
    {
        var bytes = Convert.FromHexString(hex);
        var decoder = Decoder.Create(bitness, new ByteArrayCodeReader(bytes), 0x1000);
        var native = new System.Collections.Generic.List<Iced.Intel.Instruction>();
        while (decoder.IP < 0x1000UL + (ulong)bytes.Length)
            native.Add(decoder.Decode());
        return native.ToArray();
    }
}
