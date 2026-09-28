using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64ScalarWrapperTailCallFixtureTests
{
    [Test]
    public void ExactPlayerRequiresTheUnchangedSingleScalarField()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_SCALAR_WRAPPER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_SCALAR_WRAPPER_FIXTURE_INPUT to the neutral exact player input.");

        Initialize(input!);
        try
        {
            var assembly = Cpp2IlApi.CurrentAppContext!.GetAssemblyByName("ScalarWrapperFixture")!;
            var names = new[] { "UnsignedCellA", "UnsignedCellB", "WideUnsignedCell" };
            var admitted = 0;
            foreach (var name in names)
            {
                var wrapper = assembly.Types.Single(type => type.Name == name);
                var field = wrapper.Fields.Single(candidate => !candidate.IsStatic);
                var expected = name == "WideUnsignedCell"
                    ? new[] { "ToString", "GetHashCode", "CompareTo" }
                    : ["ToString", "CompareTo"];
                foreach (var methodName in expected)
                {
                    var method = wrapper.Methods.Single(candidate => candidate.Name == methodName);
                    var proof = X64ScalarWrapperTailCallProof.Find(method);
                    Assert.That(proof, Is.Not.Null, methodName);
                    Assert.Multiple(() =>
                    {
                        Assert.That(proof!.Field, Is.SameAs(field));
                        Assert.That(proof.Target.Name, Is.EqualTo(methodName));
                        Assert.That(proof.HasValueArgument,
                            Is.EqualTo(methodName == "CompareTo"));
                    });
                    admitted++;

                    try
                    {
                        field.OverrideOffset = field.DefaultOffset + 4;
                        Assert.That(X64ScalarWrapperTailCallProof.Find(method), Is.Null);
                    }
                    finally { field.OverrideOffset = null; }

                    try
                    {
                        field.OverrideFieldType = Cpp2IlApi.CurrentAppContext!.SystemTypes.SystemInt32Type;
                        Assert.That(X64ScalarWrapperTailCallProof.Find(method), Is.Null);
                    }
                    finally { field.OverrideFieldType = null; }
                }
            }
            Assert.That(admitted, Is.EqualTo(7));
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void SharedUInt32HashTargetRemainsUnresolved()
    {
        var input = Environment.GetEnvironmentVariable("CPP2IL_SCALAR_WRAPPER_NEGATIVE_INPUT");
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set CPP2IL_SCALAR_WRAPPER_NEGATIVE_INPUT to the neutral nine-method player input.");

        Initialize(input!);
        try
        {
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName("ScalarWrapperFixture")!;
            foreach (var name in new[] { "UnsignedCellA", "UnsignedCellB" })
            {
                var method = assembly.Types.Single(type => type.Name == name)
                    .Methods.Single(candidate => candidate.Name == "GetHashCode");
                method.EnsureRawBytes();
                var shape = X64ScalarWrapperTailCallProof.TryProveShape(
                    X86Utils.Iterate(method).ToArray());
                Assert.That(shape, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(shape!.Value.HasValueArgument, Is.False);
                    Assert.That(app.MethodsByAddress[shape.Value.TargetAddress].Count,
                        Is.GreaterThan(1));
                    Assert.That(X64ScalarWrapperTailCallProof.Find(method), Is.Null);
                });
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void Initialize(string input)
    {
        var binary = Path.Combine(input, "GameAssembly.dll");
        var metadata = Path.Combine(input, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
            UnityVersion.Parse("2021.3.35f1"));
    }
}
