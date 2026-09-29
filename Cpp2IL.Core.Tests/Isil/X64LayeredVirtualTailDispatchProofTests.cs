using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using LibCpp2IL;

namespace Cpp2IL.Core.Tests.Isil;

public class X64LayeredVirtualTailDispatchProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerRequiresUnchangedClassAndInterfaceLayoutForVirtualTail()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_LAYERED_VIRTUAL_TAIL_DISPATCH_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_LAYERED_VIRTUAL_TAIL_DISPATCH_FIXTURE_INPUT to the neutral player-input directory.");
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
            var owner = app.GetAssemblyByName("LayeredVirtualTailDispatchFixture")!.Types
                .Single(type => type.Name == "LayeredNode");
            var method = owner.Methods.Single(candidate => candidate.Name == "Forward");
            var target = owner.Methods.Single(candidate => candidate.Name == "Mark");
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            Assert.That(method.RawBytes.Length, Is.EqualTo(17));
            Assert.That(native, Has.Length.EqualTo(3));
            Assert.That(owner.Definition!.InterfaceOffsets, Has.Length.EqualTo(1));
            Assert.That(owner.Definition.RawInterfaces, Has.Length.EqualTo(1));
            Assert.That(owner.BaseType, Is.Not.SameAs(app.SystemTypes.SystemObjectType));
            Assert.That(X86CallerExceptionRegionProof.Check(method, native,
                new HashSet<ulong>()), Does.Contain("unproved exit"));

            var evidence = X64VirtualTailDispatchProof.Find(method, native);
            Assert.That(evidence, Is.Not.Null);
            Assert.That(evidence!.Target, Is.SameAs(target));
            Assert.That(owner.Definition.VTable[evidence.Slot]!.AsMethod(),
                Is.SameAs(target.Definition));
            Assert.That(owner.BaseType!.Definition!.VTable.Length,
                Is.LessThanOrEqualTo(evidence.Slot),
                "The selected slot must be introduced by this owner, not override a base declaration.");

            var baseDefinition = owner.BaseType.Definition;
            var originalBaseSlotCount = baseDefinition.VtableCount;
            try
            {
                baseDefinition.VtableCount = (ushort)(evidence.Slot + 1);
                Assert.That(X64VirtualTailDispatchProof.Find(method, native), Is.Null,
                    "A selected slot inherited from the base cannot prove the managed callsite declaration.");
            }
            finally { baseDefinition.VtableCount = originalBaseSlotCount; }

            var originalBase = owner.OverrideBaseType;
            try
            {
                owner.OverrideBaseType = app.SystemTypes.SystemObjectType;
                Assert.That(X64VirtualTailDispatchProof.Find(method, native), Is.Null,
                    "A changed inheritance chain must invalidate the player layout proof.");
            }
            finally { owner.OverrideBaseType = originalBase; }

            var ancestor = owner.BaseType!;
            var originalAncestorOffsetCount = ancestor.Definition!.InterfaceOffsetsCount;
            try
            {
                ancestor.Definition.InterfaceOffsetsCount++;
                Assert.That(X64VirtualTailDispatchProof.Find(method, native), Is.Null,
                    "An inherited interface interval has no established position in the derived vtable.");
            }
            finally { ancestor.Definition.InterfaceOffsetsCount = originalAncestorOffsetCount; }

            var originalInterface = owner.InterfaceContexts[0];
            try
            {
                owner.InterfaceContexts[0] = app.SystemTypes.SystemObjectType;
                Assert.That(X64VirtualTailDispatchProof.Find(method, native), Is.Null,
                    "The resolved interface must match the raw interface and offset type.");
            }
            finally { owner.InterfaceContexts[0] = originalInterface; }

            var interfaceMethod = originalInterface.Methods.Single();
            var originalInterfaceSlot = interfaceMethod.Definition!.slot;
            try
            {
                interfaceMethod.Definition.slot = 1;
                Assert.That(X64VirtualTailDispatchProof.Find(method, native), Is.Null,
                    "An interface method outside the proved contiguous interval could occupy the selected slot.");
            }
            finally { interfaceMethod.Definition.slot = originalInterfaceSlot; }

            var interfaceOffset = owner.Definition.InterfaceOffsets[0];
            var originalOffset = interfaceOffset.offset;
            try
            {
                interfaceOffset.offset = evidence.Slot;
                Assert.That(X64VirtualTailDispatchProof.Find(method, native), Is.Null,
                    "An interface interval covering the selected slot cannot identify the class method.");
            }
            finally { interfaceOffset.offset = originalOffset; }

            var originalSlot = target.Definition!.slot;
            try
            {
                target.Definition.slot = (ushort)(evidence.Slot + 1);
                Assert.That(X64VirtualTailDispatchProof.Find(method, native), Is.Null,
                    "The raw method slot must agree with the vtable entry selected by native code.");
            }
            finally { target.Definition.slot = originalSlot; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
