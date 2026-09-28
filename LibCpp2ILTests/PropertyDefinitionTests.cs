using System.IO;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.Reflection;
using Xunit;

namespace LibCpp2ILTests;

public class PropertyDefinitionTests
{
    [Fact]
    public void SetterOnlyIndexerUsesFinalValueParameter()
    {
        var indexType = new Il2CppType();
        var valueType = new Il2CppType();
        var index = new Il2CppParameterReflectionData { RawType = indexType };
        var value = new Il2CppParameterReflectionData { RawType = valueType };

        var selected = Il2CppPropertyDefinition.GetSetterValueParameter([index, value]);

        Assert.Same(value, selected);
        Assert.Same(valueType, selected.RawType);
        Assert.NotSame(indexType, selected.RawType);
    }

    [Fact]
    public void SetterWithoutValueParameterIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => Il2CppPropertyDefinition.GetSetterValueParameter([]));
        Assert.Throws<InvalidDataException>(() => Il2CppPropertyDefinition.GetSetterValueParameter(null));
    }
}
