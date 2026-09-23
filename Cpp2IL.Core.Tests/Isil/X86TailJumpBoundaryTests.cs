using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests.Isil;

public class X86TailJumpBoundaryTests
{
    [Test]
    public void DirectJumpUsesTheMethodStartAndHalfOpenByteRange()
    {
        const ulong methodStart = 0x1000;
        const int bodyLength = 0x1A;
        Assert.Multiple(() =>
        {
            Assert.That(X86InstructionSet.TargetsOutsideMethod(0x0FFF, methodStart, bodyLength), Is.True);
            Assert.That(X86InstructionSet.TargetsOutsideMethod(0x1000, methodStart, bodyLength), Is.False);
            Assert.That(X86InstructionSet.TargetsOutsideMethod(0x1019, methodStart, bodyLength), Is.False);
            Assert.That(X86InstructionSet.TargetsOutsideMethod(0x101A, methodStart, bodyLength), Is.True);
            Assert.That(X86InstructionSet.TargetsOutsideMethod(0x1020, methodStart, bodyLength), Is.True);
        });
    }
}
