using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using IsilInstruction = Cpp2IL.Core.ISIL.Instruction;
using IsilRegister = Cpp2IL.Core.ISIL.Register;
using NativeInstruction = Iced.Intel.Instruction;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ByteMaskBooleanParameterProofTests
{
    [Test]
    public void ExactLeafReturnsTheMaskedLowBit()
    {
        var shape = X64ByteMaskBooleanParameterProof.TryProveShape(
            Decode("80E2010FB6C2C3"));
        Assert.That(shape?.End, Is.EqualTo(0x1007));
    }

    [TestCase("80E2020FB6C2C3")] // a noncanonical Boolean value of two could be returned
    [TestCase("80E6010FB6C2C3")] // DH is not the low byte of RDX
    [TestCase("80E2010FB6C1C3")] // MOVZX reads CL, not the masked DL
    [TestCase("80E2010FB6D2C3")] // MOVZX writes EDX, not the Boolean return register
    [TestCase("80E2010FBE C2 C3")] // sign extension has a different data contract
    [TestCase("80E2010FB6C290C3")] // an extra instruction before return
    [TestCase("80E2010FB6C2C20800")] // a stack-adjusting return
    [TestCase("6480E2010FB6C2C3")] // a segment prefix is outside the proved encoding
    [TestCase("80E2010FB6C2F3C3")] // a prefixed return is outside the proved encoding
    [TestCase("80E2010FB6C2")] // no complete return
    [TestCase("80E2010FB6C2C3C3")] // an unaccounted trailing instruction
    public void NeighboringByteShapesRemainUnproved(string bytes)
    {
        Assert.That(X64ByteMaskBooleanParameterProof.TryProveShape(
            Decode(bytes.Replace(" ", ""))), Is.Null);
    }

    [Test]
    public void DiscontinuousOrNon64BitInstructionsRemainUnproved()
    {
        var native = Decode("80E2010FB6C2C3");
        native[1].IP++;
        Assert.That(X64ByteMaskBooleanParameterProof.TryProveShape(native), Is.Null);
        Assert.That(X64ByteMaskBooleanParameterProof.TryProveShape(
            Decode("80E2010FB6C2C3", bitness: 32)), Is.Null);
    }

    [Test]
    public void EstimatedSpanEndsAtAnIndependentUnmanagedFunctionAfterTrapAlignment()
    {
        var native = Decode("80E2010FB6C2C3" + new string('C', 18) + "90C3");
        var unwind = LeafIndex(0x10);
        Assert.That(X64ByteMaskBooleanParameterProof.TryProveShape(native), Is.Null,
            "an estimated method span is not itself the semantic body");
        Assert.That(X64ByteMaskBooleanParameterProof.TryProveLeafExtent(native,
            0x1007, native[^1].NextIP, unwind), Is.EqualTo(0x1010));
        Assert.That(X64ByteMaskBooleanParameterProof.TryProveLeafExtent(
            native.Take(3).ToArray(), 0x1007, 0x1007, unwind), Is.EqualTo(0x1007));
    }

    [TestCase("unknown-boundary")]
    [TestCase("interior-unwind")]
    [TestCase("discontinuous-padding")]
    [TestCase("nonpadding-gap")]
    [TestCase("discontinuous-next")]
    [TestCase("truncated-next")]
    [TestCase("excessive-padding")]
    public void EstimatedSpanCannotDiscardUnprovedBytesOrBoundaries(string defect)
    {
        var native = Decode("80E2010FB6C2C3" + new string('C',
            defect == "excessive-padding" ? 32 : 18) + "90C3");
        var unwind = LeafIndex(defect == "unknown-boundary" ? null :
            defect == "interior-unwind" ? 8U : 0x10U);
        var rawEnd = native[^1].NextIP;
        switch (defect)
        {
            case "discontinuous-padding": native[3].IP++; break;
            case "nonpadding-gap": native[3].Code = Code.Nopd; break;
            case "discontinuous-next": native[12].IP++; break;
            case "truncated-next": rawEnd = 0x1010; break;
        }
        Assert.That(X64ByteMaskBooleanParameterProof.TryProveLeafExtent(native,
            0x1007, rawEnd, unwind), Is.Null);
    }

    [Test]
    [NonParallelizable]
    public void ExactPlayerRequiresUnchangedUnsignedByteEnumAndCompleteLeaf()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_BYTE_MASK_ONE_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_BYTE_MASK_ONE_FIXTURE_INPUT to the synthetic player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("ByteMaskOneFixture")!.Types
                .SelectMany(type => type.Methods)
                .Single(candidate => candidate.Name == "HasOne");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(X64ByteMaskBooleanParameterProof.TryProveShape(native.Take(3).ToArray()), Is.Not.Null);
            Assert.That(X64ByteMaskBooleanParameterProof.Find(method, native), Is.True);
            var lifted = X64ByteMaskBooleanParameterProof.TryLift(method, native);
            Assert.That(lifted?.Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { ISIL.OpCode.IntegerExtend, ISIL.OpCode.And,
                    ISIL.OpCode.CheckNotEqual, ISIL.OpCode.Return }));
            Assert.That(lifted!.Select(instruction => instruction.NativeAddress),
                Is.EqualTo(new[] { native[0].IP, native[0].IP, native[1].IP, native[2].IP }));
            Assert.That(lifted[0].Operands.Skip(2).Cast<Immediate>().Select(operand => operand.Value),
                Is.EqualTo(new long[] { 8, 32, 0 }));
            foreach (var highBits in new[] { 0UL, 0x100UL, 0xFFFFFFFFFFFFFF00UL })
                for (var value = 0; value <= byte.MaxValue; value++)
                    Assert.That(Evaluate(lifted, highBits | (ulong)(byte)value),
                        Is.EqualTo((value & 1) != 0), $"byte {value}, upper bits {highBits:X}");

            method.Analyze();
            var extension = method.ControlFlowGraph!.Instructions.Single(instruction =>
                instruction.OpCode == ISIL.OpCode.IntegerExtend);
            Assert.That(IntegerExtension.IsPureAndValid(extension), Is.True);
            var enumType = method.Parameters[1].ParameterType;
            Assert.That(((LocalVariable)extension.Operands[1]).Type, Is.SameAs(enumType),
                "the absent ECX local must not shift the enum parameter's ABI slot");
            try
            {
                enumType.OverrideEnumUnderlyingType = app.SystemTypes.SystemSByteType;
                Assert.That(X64ByteMaskBooleanParameterProof.Find(method, native), Is.False);
                Assert.That(IntegerExtension.IsPureAndValid(extension), Is.False);
            }
            finally { enumType.OverrideEnumUnderlyingType = null; }
            try
            {
                method.Parameters[1].OverrideParameterType = app.SystemTypes.SystemByteType;
                Assert.That(X64ByteMaskBooleanParameterProof.Find(method, native), Is.False);
            }
            finally { method.Parameters[1].OverrideParameterType = null; }
            var valueField = enumType.Fields.Single(field => !field.IsStatic);
            try
            {
                valueField.OverrideFieldType = app.SystemTypes.SystemSByteType;
                Assert.That(X64ByteMaskBooleanParameterProof.Find(method, native), Is.False);
                Assert.That(IntegerExtension.IsPureAndValid(extension), Is.False);
            }
            finally { valueField.OverrideFieldType = null; }
            try
            {
                method.OverrideReturnType = app.SystemTypes.SystemInt32Type;
                Assert.That(X64ByteMaskBooleanParameterProof.Find(method, native), Is.False);
            }
            finally { method.OverrideReturnType = null; }
            var interior = method.UnderlyingPointer + 1;
            Assert.That(app.MethodsByAddress.ContainsKey(interior), Is.False);
            try
            {
                app.MethodsByAddress.Add(interior, [method]);
                Assert.That(X64ByteMaskBooleanParameterProof.Find(method, native), Is.False,
                    "another managed entry invalidates the complete leaf's boundary");
            }
            finally { app.MethodsByAddress.Remove(interior); }
            var paddingEntry = native[2].NextIP;
            if (native.Length > 3 && native[3].Code == Code.Int3)
            {
                try
                {
                    app.MethodsByAddress.Add(paddingEntry, [method]);
                    Assert.That(X64ByteMaskBooleanParameterProof.Find(method, native), Is.False,
                        "a managed entry in discarded alignment bytes invalidates the boundary");
                }
                finally { app.MethodsByAddress.Remove(paddingEntry); }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static X64UnwindProof.Index LeafIndex(uint? followingStart) =>
        new(0x1000, 0x100,
            [new X64UnwindProof.Section(0, 0x100, 0, 0x100, 0x60000020)],
            followingStart is { } start ? [new X64UnwindProof.Function(start, 0x20, null)] : []);

    private static bool Evaluate(IEnumerable<IsilInstruction> body, ulong argument)
    {
        var values = new Dictionary<IsilRegister, ulong> { [new(null, "rdx")] = argument };
        foreach (var instruction in body)
        {
            if (instruction.OpCode == ISIL.OpCode.Return)
                return values[(IsilRegister)instruction.Operands[0]] != 0;
            var destination = (IsilRegister)instruction.Operands[0];
            var source = values[(IsilRegister)instruction.Operands[1]];
            values[destination] = instruction.OpCode switch
            {
                ISIL.OpCode.IntegerExtend => source &
                    ((1UL << (int)((Immediate)instruction.Operands[2]).Value) - 1),
                ISIL.OpCode.And => source & (ulong)((Immediate)instruction.Operands[2]).Value,
                ISIL.OpCode.CheckNotEqual => source !=
                    (ulong)((Immediate)instruction.Operands[2]).Value ? 1UL : 0UL,
                _ => throw new InvalidOperationException("Unexpected operation in the closed byte predicate")
            };
        }
        throw new InvalidOperationException("Missing Boolean return");
    }

    private static NativeInstruction[] Decode(string hex, int bitness = 64)
    {
        var bytes = Convert.FromHexString(hex);
        var decoder = Decoder.Create(bitness, new ByteArrayCodeReader(bytes), 0x1000);
        var body = new List<NativeInstruction>();
        while (decoder.IP < 0x1000 + (ulong)bytes.Length)
            body.Add(decoder.Decode());
        return body.ToArray();
    }
}
