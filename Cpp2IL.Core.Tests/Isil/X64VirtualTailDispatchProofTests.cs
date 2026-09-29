using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;

namespace Cpp2IL.Core.Tests.Isil;

public class X64VirtualTailDispatchProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerBindsCompleteVirtualTailAndRejectsAlteredExitOrMetadata()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_VIRTUAL_TAIL_DISPATCH_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_VIRTUAL_TAIL_DISPATCH_FIXTURE_INPUT to the neutral player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("VirtualTailDispatchFixture")!.Types
                .Single(type => type.Name == "DispatchNode");
            var method = owner.Methods.Single(candidate => candidate.Name == "Forward");
            var target = owner.Methods.Single(candidate => candidate.Name == "Mark");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(method.RawBytes.Length, Is.EqualTo(17));
            Assert.That(native, Has.Length.EqualTo(3));
            Assert.That(X64VirtualTailDispatchProof.TryProveShape(native)?.MethodPointerOffset,
                Is.EqualTo(0x178UL));
            Assert.That(owner.Definition!.VTable[4]!.AsMethod(), Is.SameAs(target.Definition));
            Assert.That(X86CallerExceptionRegionProof.Check(method, native,
                new HashSet<ulong>()), Does.Contain("unproved exit"));
            Assert.That(X86CallerExceptionRegionProof.CheckProvedTerminalIndirectBranch(
                method, native, native[2].IP), Is.Null);
            Assert.That(X86CallerExceptionRegionProof.CheckProvedTerminalIndirectBranch(
                method, native, native[1].IP), Does.Contain("unproved exit"));

            var evidence = X64VirtualTailDispatchProof.Find(method, native);
            Assert.That(evidence, Is.Not.Null);
            Assert.That(evidence!.Target, Is.SameAs(target));
            Assert.That(evidence.Slot, Is.EqualTo(4));
            Assert.That(owner.BaseType, Is.SameAs(app.SystemTypes.SystemObjectType));
            var originalInterfaceCount = owner.Definition.InterfacesCount;
            var originalOffsetCount = owner.Definition.InterfaceOffsetsCount;
            try
            {
                owner.Definition.InterfacesCount++;
                Assert.That(X64VirtualTailDispatchProof.Find(method, native), Is.Null,
                    "An inconsistent direct-interface count cannot prove the vtable slot.");
                owner.Definition.InterfacesCount = originalInterfaceCount;
                owner.Definition.InterfaceOffsetsCount++;
                Assert.That(X64VirtualTailDispatchProof.Find(method, native), Is.Null,
                    "An inconsistent interface-offset count cannot prove the vtable slot.");
            }
            finally
            {
                owner.Definition.InterfacesCount = originalInterfaceCount;
                owner.Definition.InterfaceOffsetsCount = originalOffsetCount;
            }
            var wrongMethodInfo = native.ToArray();
            wrongMethodInfo[1].MemoryDisplacement64 += 8;
            Assert.That(X64VirtualTailDispatchProof.TryProveShape(wrongMethodInfo), Is.Null);
            Assert.That(X64VirtualTailDispatchProof.Find(method, wrongMethodInfo), Is.Null);
            var wrongSlot = native.ToArray();
            wrongSlot[2].MemoryDisplacement64 += 16;
            Assert.That(X64VirtualTailDispatchProof.TryProveShape(wrongSlot), Is.Null);
            var wrongReceiver = native.ToArray();
            wrongReceiver[0].MemoryBase = Register.RDX;
            Assert.That(X64VirtualTailDispatchProof.TryProveShape(wrongReceiver), Is.Null);
            var wrongExit = native.ToArray();
            wrongExit[2].Code = Code.Jmp_rel32_64;
            Assert.That(X64VirtualTailDispatchProof.TryProveShape(wrongExit), Is.Null);
            Assert.That(X64VirtualTailDispatchProof.Find(method,
                native.Append(native[2]).ToArray()), Is.Null,
                "A reachable instruction after the alleged tail is outside the closed leaf.");

            try
            {
                target.Attributes |= MethodAttributes.Final;
                Assert.That(X64VirtualTailDispatchProof.Find(method, native), Is.Null);
            }
            finally { target.Attributes = target.DefaultAttributes; }
            var aliases = app.MethodsByAddress[method.UnderlyingPointer];
            aliases.Add(target);
            try
            {
                Assert.That(X64VirtualTailDispatchProof.Find(method, native), Is.Null,
                    "A shared entry lacks a unique caller identity in this bounded proof.");
            }
            finally { aliases.Remove(target); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
