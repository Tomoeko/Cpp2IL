using System.Reflection;
using LibCpp2IL.Metadata;
using Xunit;

namespace LibCpp2ILTests;

public class MethodImplementationAttributeTests
{
    [Theory]
    [InlineData(24.5f)]
    [InlineData(29f)]
    [InlineData(31.1f)]
    [InlineData(106f)]
    [InlineData(108f)]
    public void InternalCallRetainsImplementationFlags(float metadataVersion)
    {
        MethodImplAttributes[] implementations =
        [
            MethodImplAttributes.IL,
            MethodImplAttributes.Runtime,
            MethodImplAttributes.Unmanaged,
            MethodImplAttributes.PreserveSig | MethodImplAttributes.NoInlining,
        ];
        foreach (var implementation in implementations)
        {
            var expected = implementation | MethodImplAttributes.InternalCall;
            var method = new Il2CppMethodDefinition { MetadataVersion = metadataVersion, iflags = (ushort)expected };

            Assert.False(method.IsUnmanagedCallersOnly);
            Assert.Equal(expected, method.MethodImplAttributes);
            Assert.True((method.MethodImplAttributes & MethodImplAttributes.InternalCall) != 0);
        }
    }

    [Fact]
    public void IncompleteExtensionMarkerPreservesEveryRawFlag()
    {
        // Unknown reserved bits must not erase evidence about the method's implementation.
        for (var raw = 0; raw < 0xF000; raw++)
        {
            var method = new Il2CppMethodDefinition { MetadataVersion = 29, iflags = (ushort)raw };

            Assert.False(method.IsUnmanagedCallersOnly);
            Assert.Equal((MethodImplAttributes)raw, method.MethodImplAttributes);
        }
    }

    [Fact]
    public void CompleteExtensionMarkerRetainsLowerImplementationBits()
    {
        for (var lowerFlags = 0; lowerFlags <= 0x0FFF; lowerFlags++)
        {
            var method = new Il2CppMethodDefinition { MetadataVersion = 106, iflags = (ushort)(0xF000 | lowerFlags) };

            Assert.True(method.IsUnmanagedCallersOnly);
            Assert.Equal((MethodImplAttributes)lowerFlags, method.MethodImplAttributes);
        }
    }
}
