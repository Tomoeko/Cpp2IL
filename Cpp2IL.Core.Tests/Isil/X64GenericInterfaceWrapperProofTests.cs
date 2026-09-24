using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64GenericInterfaceWrapperProofTests
{
    [Test]
    public void ExactPlayerBindsMethodRefDespiteThreeTailAliases()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_GENERIC_DISPATCH_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_GENERIC_DISPATCH_FIXTURE_INPUT to the neutral player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        var binaryBytes = File.ReadAllBytes(binary);
        var metadataBytes = File.ReadAllBytes(metadata);
        var changedSlot = (byte[])binaryBytes.Clone();
        var changedTypeArgument = (byte[])binaryBytes.Clone();
        var changedTail = (byte[])binaryBytes.Clone();
        var relocatedMethodSlot = (byte[])binaryBytes.Clone();

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("GenericDispatchFixture")!.Types
                .Single(type => type.Name == "Forwarder");
            var wrappers = owner.Methods.Where(method =>
                method.Name.Contains("Read", StringComparison.Ordinal)).ToArray();
            Assert.That(wrappers, Has.Length.EqualTo(2));
            var pe = (PE)app.Binary;

            foreach (var wrapper in wrappers)
            {
                var expectedKey = wrapper.ReturnType.FullName == "System.String" ? 17 : -7;
                var evidence = X64GenericInterfaceWrapperProof.Find(wrapper);
                Assert.That(evidence, Is.Not.Null, wrapper.FullNameWithSignature);
                Assert.That(evidence!.Argument, Is.EqualTo(expectedKey));
                Assert.That(evidence.Target.MethodGenericParameters,
                    Is.EqualTo(new[] { wrapper.ReturnType }));

                wrapper.EnsureRawBytes();
                var native = X86Utils.Iterate(wrapper).ToArray();
                Assert.That(X64GenericInterfaceWrapperProof.TryProveShape(native,
                    out var shape), Is.True);
                Assert.That(shape.Argument, Is.EqualTo(expectedKey));
                Assert.That(app.MethodsByAddress[shape.Target], Has.Count.EqualTo(3));
                Assert.That(app.MethodsByAddress[shape.Target].Count(candidate =>
                    ReferenceEquals(candidate, evidence.Target.BaseMethodContext)),
                    Is.EqualTo(1));

                foreach (var defect in new[] { "branch", "once write", "MethodInfo load",
                             "signed argument register", "receiver", "tail kind" })
                {
                    var changed = native.ToArray();
                    switch (defect)
                    {
                        case "branch": changed[4].NearBranch64++; break;
                        case "once write": changed[7].Immediate8 = 0; break;
                        case "MethodInfo load": changed[8].Op0Register = Register.RDX; break;
                        case "signed argument register": changed[9].Op0Register = Register.EAX; break;
                        case "receiver": changed[10].Op1Register = Register.RDX; break;
                        case "tail kind": changed[13].Code = Code.Call_rel32_64; break;
                    }
                    Assert.That(X64GenericInterfaceWrapperProof.TryProveShape(changed,
                        out _), Is.False, defect);
                }

                var originalAttributes = wrapper.Attributes;
                try
                {
                    wrapper.Attributes &= ~MethodAttributes.Final;
                    Assert.That(X64GenericInterfaceWrapperProof.Find(wrapper), Is.Null);
                }
                finally { wrapper.Attributes = originalAttributes; }

                var originalReturn = wrapper.OverrideReturnType;
                try
                {
                    wrapper.OverrideReturnType = app.SystemTypes.SystemObjectType;
                    Assert.That(X64GenericInterfaceWrapperProof.Find(wrapper), Is.Null);
                }
                finally { wrapper.OverrideReturnType = originalReturn; }

                var originalBitfield = owner.Definition!.Bitfield;
                try
                {
                    owner.Definition.Bitfield |= 1u << 3;
                    Assert.That(X64GenericInterfaceWrapperProof.Find(wrapper), Is.Null);
                }
                finally { owner.Definition.Bitfield = originalBitfield; }

                var entryAliases = app.MethodsByAddress[wrapper.UnderlyingPointer];
                entryAliases.Add(evidence.Target.BaseMethodContext);
                try { Assert.That(X64GenericInterfaceWrapperProof.Find(wrapper), Is.Null); }
                finally { entryAliases.RemoveAt(entryAliases.Count - 1); }

                var targetAliases = app.MethodsByAddress[shape.Target];
                var targetIndex = targetAliases.IndexOf(evidence.Target.BaseMethodContext);
                Assert.That(targetIndex, Is.GreaterThanOrEqualTo(0));
                targetAliases.RemoveAt(targetIndex);
                try { Assert.That(X64GenericInterfaceWrapperProof.Find(wrapper), Is.Null); }
                finally { targetAliases.Insert(targetIndex, evidence.Target.BaseMethodContext); }
                targetAliases.Add(evidence.Target.BaseMethodContext);
                try { Assert.That(X64GenericInterfaceWrapperProof.Find(wrapper), Is.Null); }
                finally { targetAliases.RemoveAt(targetAliases.Count - 1); }

                var baseOwner = evidence.Target.BaseMethodContext.DeclaringType!;
                var originalBaseBitfield = baseOwner.Definition!.Bitfield;
                try
                {
                    baseOwner.Definition.Bitfield |= 1u << 3;
                    Assert.That(X64GenericInterfaceWrapperProof.Find(wrapper), Is.Null);
                }
                finally { baseOwner.Definition.Bitfield = originalBaseBitfield; }

                var baseInitializer = new InjectedMethodAnalysisContext(baseOwner,
                    ".cctor", app.SystemTypes.SystemVoidType,
                    MethodAttributes.Private | MethodAttributes.Static, []);
                baseOwner.Methods.Add(baseInitializer);
                try { Assert.That(X64GenericInterfaceWrapperProof.Find(wrapper), Is.Null); }
                finally { baseOwner.Methods.Remove(baseInitializer); }
            }

            var payload = wrappers.Single(method => method.ReturnType.FullName != "System.String");
            var text = wrappers.Single(method => method.ReturnType.FullName == "System.String");
            var payloadNative = X86Utils.Iterate(payload).ToArray();
            Assert.That(X64GenericInterfaceWrapperProof.TryProveShape(
                payloadNative, out var payloadShape), Is.True);
            Assert.That(X64GenericInterfaceWrapperProof.TryProveShape(
                X86Utils.Iterate(text).ToArray(), out var textShape), Is.True);

            PatchRipTarget(changedSlot, pe, payloadNative[8], textShape.MethodSlot);
            PatchRipTarget(changedTypeArgument, pe, payloadNative[5], textShape.MethodSlot);
            PatchRipTarget(changedTypeArgument, pe, payloadNative[8], textShape.MethodSlot);

            var tailRaw = checked((int)pe.MapVirtualAddressToRaw(payloadNative[13].IP,
                false));
            Assert.That(changedTail[tailRaw], Is.EqualTo(0xE9));
            changedTail[tailRaw + 1] ^= 1;

            PoisonRelocation(relocatedMethodSlot, pe,
                X64UnwindProof.ForApplication(app)!.ImageBase,
                payloadShape.MethodSlot);
        }
        finally { Cpp2IlApi.ResetInternalState(); }

        AssertRejected(changedSlot, metadataBytes, expectedShape: false);
        AssertRejected(changedTypeArgument, metadataBytes,
            expectedShape: true, wrongMethodArgument: true);
        AssertRejected(changedTail, metadataBytes, expectedShape: true);
        AssertRejected(relocatedMethodSlot, metadataBytes,
            expectedShape: true, relocatedSlot: true);
    }

    private static void PatchRipTarget(byte[] binary, PE pe, Instruction instruction,
        ulong target)
    {
        Assert.That(instruction.Length, Is.EqualTo(7));
        var raw = checked((int)pe.MapVirtualAddressToRaw(instruction.IP, false));
        BinaryPrimitives.WriteInt32LittleEndian(binary.AsSpan(raw + 3, 4),
            checked((int)((long)target - (long)instruction.NextIP)));
    }

    private static void PoisonRelocation(byte[] binary, PE pe,
        ulong imageBase, ulong slot)
    {
        var peHeader = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            binary.AsSpan(0x3C, 4)));
        var optional = peHeader + 24;
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(binary.AsSpan(optional, 2)),
            Is.EqualTo(0x20B));
        var directory = optional + 112 + 5 * 8;
        var relocationRva = BinaryPrimitives.ReadUInt32LittleEndian(
            binary.AsSpan(directory, 4));
        var relocationSize = BinaryPrimitives.ReadUInt32LittleEndian(
            binary.AsSpan(directory + 4, 4));
        var at = checked((int)pe.MapVirtualAddressToRaw(imageBase + relocationRva, false));
        var end = checked(at + (int)relocationSize);
        var slotRva = checked((uint)(slot - imageBase));
        var page = slotRva & ~0xFFFu;
        var chosenBlock = -1;
        var chosenPage = 0u;
        while (at < end)
        {
            var blockPage = BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(at, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan(at + 4, 4));
            Assert.That(size, Is.GreaterThanOrEqualTo(10));
            if (blockPage < page && (chosenBlock < 0 || blockPage > chosenPage))
            {
                chosenBlock = at;
                chosenPage = blockPage;
            }
            at = checked(at + (int)size);
        }
        Assert.That(at, Is.EqualTo(end));
        Assert.That(chosenBlock, Is.GreaterThanOrEqualTo(0));
        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(
            binary.AsSpan(chosenBlock + 4, 4));
        for (var entry = chosenBlock + 8; entry < chosenBlock + blockSize; entry += 2)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(binary.AsSpan(entry, 2));
            if (value >> 12 != 10)
                continue;
            BinaryPrimitives.WriteUInt32LittleEndian(binary.AsSpan(chosenBlock, 4), page);
            BinaryPrimitives.WriteUInt16LittleEndian(binary.AsSpan(entry, 2),
                checked((ushort)(0xA000 | (slotRva & 0xFFF))));
            return;
        }
        Assert.Fail("Exact player has no x64 relocation entry to mutate.");
    }

    private static void AssertRejected(byte[] binary, byte[] metadata,
        bool expectedShape, bool wrongMethodArgument = false,
        bool relocatedSlot = false)
    {
        Cpp2IlApi.ResetInternalState();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var method = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("GenericDispatchFixture")!.Types
                .Single(type => type.Name == "Forwarder").Methods
                .Single(candidate => candidate.Name.Contains("Read", StringComparison.Ordinal) &&
                                     candidate.ReturnType.FullName != "System.String");
            method.EnsureRawBytes();
            Assert.That(X64GenericInterfaceWrapperProof.TryProveShape(
                X86Utils.Iterate(method).ToArray(), out var shape), Is.EqualTo(expectedShape));
            if (wrongMethodArgument)
            {
                var app = method.AppContext;
                var usage = app.LibCpp2IlContext.GetMethodGlobalByAddress(shape.MethodSlot);
                Assert.That(usage?.Type, Is.EqualTo(MetadataUsageType.MethodRef));
                Assert.That(app.ResolveIl2CppType(usage!.AsGenericMethodRef()
                    .MethodGenericParams[0]), Is.Not.SameAs(method.ReturnType));
            }
            if (relocatedSlot)
            {
                var app = method.AppContext;
                var pe = (PE)app.Binary;
                var unwind = X64UnwindProof.ForApplication(app)!;
                Assert.That(X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind,
                    shape.MethodSlot, 8), Is.False);
                Assert.That(app.LibCpp2IlContext.GetMethodGlobalByAddress(
                    shape.MethodSlot)?.Type, Is.EqualTo(MetadataUsageType.MethodRef));
            }
            Assert.That(X64GenericInterfaceWrapperProof.Find(method), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
