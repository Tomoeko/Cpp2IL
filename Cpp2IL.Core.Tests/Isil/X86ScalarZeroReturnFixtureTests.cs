using System;
using System.IO;
using System.Linq;
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
}
