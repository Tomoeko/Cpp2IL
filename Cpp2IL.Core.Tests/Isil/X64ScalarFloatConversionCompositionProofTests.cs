using System;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ScalarFloatConversionCompositionProofTests
{
    [TestCase("0F57C0F20F5AC3F30F5EC2C3", false, false)]
    [TestCase("0F57C90F57C0F20F5ACBF30F5ECA0F2FC176070F570D000000000F28C1C3", false, true)]
    [TestCase("F20F104110660F5AC0F30F5EC2C3", true, false)]
    [TestCase("F20F1049100F57C0660F5AC9F30F5ECA0F2FC176070F570D000000000F28C1C3", true, true)]
    public void ClosedCompositionBindsEachScalarOperationAndOrderedNegativeArm(string hex, bool field, bool negative)
    {
        var proof = X64ScalarFloatConversionCompositionProof.TryProveShape(Decode(hex));
        Assert.That(proof, Is.Not.Null);
        Assert.That(proof!.FieldLoad.HasValue, Is.EqualTo(field));
        Assert.That(proof.SignFlip.HasValue, Is.EqualTo(negative));
        Assert.That(proof.Division.Op0Register, Is.EqualTo(proof.Conversion.Op0Register));
    }

    [Test]
    public void SyntheticRegisterAllocationCanReturnTheQuotientDirectly()
    {
        // Derive another allocation from the neutral fixture instruction model,
        // rather than retaining byte evidence from an application binary.
        var allocated = Decode("0F57C90F57C0F20F5ACBF30F5ECA0F2FC176070F570D000000000F28C1C3");
        var body = allocated.Where(instruction => instruction.Code != Code.Movaps_xmm_xmmm128).ToArray();
        ulong address = 0x1000;
        for (var index = 0; index < body.Length; index++)
        {
            var instruction = body[index];
            instruction.IP = address;
            if (instruction.Op0Kind == OpKind.Register) instruction.Op0Register = Swap(instruction.Op0Register);
            if (instruction.Op1Kind == OpKind.Register) instruction.Op1Register = Swap(instruction.Op1Register);
            body[index] = instruction;
            address = instruction.NextIP;
        }
        var branch = body[5];
        branch.NearBranch64 = body[^1].IP;
        body[5] = branch;
        var proof = X64ScalarFloatConversionCompositionProof.TryProveShape(body);
        Assert.That(proof, Is.Not.Null);
        Assert.That(proof!.SignFlip.HasValue, Is.True);
        Assert.That(proof.ReturnCopy.HasValue, Is.False);
        return;

        static Register Swap(Register register) => register == Register.XMM0 ? Register.XMM1 :
            register == Register.XMM1 ? Register.XMM0 : register;
    }

    [TestCase("0F57C0F20F5AC0F30F5EC2C3")] // Clearing the incoming conversion source loses its value.
    [TestCase("0F57C0F20F5AC3F30F5EC0C3")] // Division by its own converted result is different.
    [TestCase("0F57C0F20F5AC3F20F5EC2C3")] // Binary64 division has different rounding.
    [TestCase("0F57C0F20F5AC3F30F5EC2E800000000C3")] // A call cannot disappear.
    [TestCase("0F57C90F57C0F20F5ACBF30F5ECA0F2FC176060F570D000000000F28C1C3")] // Interior target bypasses the expected join.
    [TestCase("0F57C90F57C0F20F5ACBF30F5ECA0F2FC177070F570D000000000F28C1C3")] // JA changes zero and unordered outcomes.
    [TestCase("0F57C90F57C0F20F5ACBF30F5ECA0F2FC876070F570D000000000F28C1C3")] // Reversed floating comparison changes the sign arm.
    [TestCase("F20F1041100F57C0660F5AC0F30F5EC2C3")] // Clearing the field load cannot retain its input.
    [TestCase("660F5AC0F30F5EC2C3")] // An unbound packed source has unknown extra lanes.
    [TestCase("F20F104110660F5AC0F30F5EC2F20F1102C3")] // Vector state cannot escape through memory.
    [TestCase("F20F104114660F5AC0F30F5EC2C3")] // Ordinary Double field storage requires natural alignment.
    [TestCase("F20F104510660F5AC0F30F5EC2C3")] // Another pointer lacks incoming-owner provenance.
    public void CompositionRejectsLostInputsPrecisionEdgesEffectsOrLaneEscape(string hex)
        => Assert.That(X64ScalarFloatConversionCompositionProof.TryProveShape(Decode(hex)), Is.Null);

    [Test]
    public void SignMaskRequiresAlignedMemoryAndContiguousCompleteInstructions()
    {
        var body = Decode("0F57C90F57C0F20F5ACBF30F5ECA0F2FC176070F570D000000000F28C1C3");
        var original = body[6];
        var changed = original;
        changed.MemoryDisplacement64++;
        body[6] = changed;
        Assert.That(X64ScalarFloatConversionCompositionProof.TryProveShape(body), Is.Null);
        body[6] = original;
        changed = body[^1];
        changed.IP++;
        body[^1] = changed;
        Assert.That(X64ScalarFloatConversionCompositionProof.TryProveShape(body), Is.Null);
    }

    private static Instruction[] Decode(string hex)
    {
        var body = Decoder.Create(64, new ByteArrayCodeReader(Convert.FromHexString(hex)), 0x1000).ToArray();
        for (var index = 0; index < body.Length; index++)
            if (body[index].IsIPRelativeMemoryOperand)
            {
                var mask = body[index];
                mask.MemoryDisplacement64 = 0x2000;
                body[index] = mask;
            }
        return body;
    }
}
