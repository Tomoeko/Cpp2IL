using Cpp2IL.Core.InstructionSets;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Tests.Isil;

public class X64ReferenceSetterDescriptorTests
{
    [TestCase("data")]
    [TestCase("union")]
    [TestCase("type-bits")]
    [TestCase("attribute-bits")]
    [TestCase("modifiers")]
    [TestCase("byref")]
    [TestCase("pinned")]
    [TestCase("value-type")]
    public void IncompleteReferenceDescriptorsDeclineBeforeLazyResolution(string mutation)
    {
        var raw = new Il2CppType
        {
            Type = Il2CppTypeEnum.IL2CPP_TYPE_CLASS, Bits = (uint)Il2CppTypeEnum.IL2CPP_TYPE_CLASS << 16,
            Datapoint = 3, Data = new Il2CppType.Union { Dummy = 3 }
        };
        Assert.That(X64InstanceReferenceSetterProof.OriginalDescriptor(raw), Is.True);
        switch (mutation)
        {
            case "data": raw.Data = null!; break;
            case "union": raw.Data.Dummy++; break;
            case "type-bits": raw.Bits ^= 1U << 16; break;
            case "attribute-bits": raw.Attrs = 1; break;
            case "modifiers": raw.NumMods = 1; break;
            case "byref": raw.Byref = 1; break;
            case "pinned": raw.Pinned = 1; break;
            case "value-type": raw.ValueType = 1; break;
        }
        Assert.That(X64InstanceReferenceSetterProof.OriginalDescriptor(raw), Is.False);
    }
}
