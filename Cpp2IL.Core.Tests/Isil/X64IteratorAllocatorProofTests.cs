using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X64IteratorAllocatorProofTests
{
    [Test]
    public void ExportedAllocatorAndCodegenTransferMustReachTheSameInternalCall()
    {
        var (export, stub) = SyntheticBodies();
        Assert.That(X64IteratorAllocatorProof.TryProveShape(export, stub), Is.True);

        var wrongExportCall = export.ToArray();
        wrongExportCall[1].NearBranch64++;
        Assert.That(X64IteratorAllocatorProof.TryProveShape(wrongExportCall, stub), Is.False);

        var wrongExportExit = export.ToArray();
        wrongExportExit[2].NearBranch64 = export[3].IP;
        Assert.That(X64IteratorAllocatorProof.TryProveShape(wrongExportExit, stub), Is.False);

        var wrongCodegenTransfer = stub.ToArray();
        wrongCodegenTransfer[0].NearBranch64++;
        Assert.That(X64IteratorAllocatorProof.TryProveShape(export, wrongCodegenTransfer), Is.False);

        var extraExportEffect = new List<Instruction>(export) { export[5] };
        Assert.That(X64IteratorAllocatorProof.TryProveShape(extraExportEffect, stub), Is.False);
    }

    private static (Instruction[] Export, Instruction[] Stub) SyntheticBodies()
    {
        const ulong exportAddress = 0x1000;
        const ulong stubAddress = 0x2000;
        const ulong internalAllocator = 0x4000;
        var exportBytes = new List<byte>();
        exportBytes.AddRange([0x48, 0x83, 0xEC, 0x28]); // reserve a Win64 call frame
        exportBytes.Add(0xE8);
        exportBytes.AddRange(BitConverter.GetBytes(unchecked((int)(internalAllocator -
            (exportAddress + (ulong)exportBytes.Count + 4)))));
        exportBytes.AddRange([0xEB, 0x02, 0x31, 0xC0]); // skip an exported failure return
        exportBytes.AddRange([0x48, 0x83, 0xC4, 0x28, 0xC3]);

        var stubBytes = new List<byte> { 0xE9 };
        stubBytes.AddRange(BitConverter.GetBytes(unchecked((int)(internalAllocator -
            (stubAddress + 5)))));
        return (Decode(exportBytes.ToArray(), exportAddress), Decode(stubBytes.ToArray(), stubAddress));
    }

    private static Instruction[] Decode(byte[] bytes, ulong start)
    {
        var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), start);
        var instructions = new List<Instruction>();
        while (decoder.IP < start + (ulong)bytes.Length)
            instructions.Add(decoder.Decode());
        return instructions.ToArray();
    }
}
