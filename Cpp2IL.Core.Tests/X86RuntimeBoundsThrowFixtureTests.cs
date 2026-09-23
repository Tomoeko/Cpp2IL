using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using IsilOpCode = Cpp2IL.Core.ISIL.OpCode;

namespace Cpp2IL.Core.Tests;

/// <summary>Optional proof gate using a locally built, redistributable source fixture.</summary>
[NonParallelizable]
public class X86RuntimeBoundsThrowFixtureTests
{
    [TestCase("Read", false, 4, false)]
    [TestCase("Write", true, 4, false)]
    [TestCase("ReadUnsigned", false, 4, true)]
    [TestCase("WriteUnsigned", true, 4, true)]
    [TestCase("ReadWide", false, 8, false)]
    [TestCase("WriteWide", true, 8, false)]
    [TestCase("ReadWideUnsigned", false, 8, true)]
    [TestCase("WriteWideUnsigned", true, 8, true)]
    public void ExactArrayAccessIdentifiesBothRuntimeExceptionExits(string name, bool isWrite, int elementSize, bool unsigned)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ARRAY_ACCESS_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ARRAY_ACCESS_FIXTURE_INPUT to the public ArrayAccessFixture player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var fixture = app.GetAssemblyByName("ArrayAccessFixture");
            Assert.That(fixture, Is.Not.Null);
            var access = fixture!.Types.SelectMany(type => type.Methods).Single(method => method.Name == name);
            var calls = X86Utils.Iterate(access).Where(instruction => instruction.Code == Code.Call_rel32_64).ToArray();
            Assert.That(calls.Where(instruction =>
                X86RuntimeBoundsThrowProof.TryIdentify(app, instruction.NearBranchTarget)).ToArray(), Has.Length.EqualTo(1));
            Assert.That(calls.Where(instruction =>
                X86RuntimeNullThrowProof.TryIdentify(app, instruction.NearBranchTarget) != null).ToArray(), Has.Length.EqualTo(1));
            var native = X86Utils.Iterate(access).ToArray();
            Assert.That(X86IntegerArrayAccessProof.TryProveShape(native, isWrite, elementSize), Is.True);
            Assert.That(X86IntegerArrayAccessProof.TryProveShape(native.Take(12).ToArray(), isWrite, elementSize), Is.True,
                "The final proven nonreturning bounds call needs no trailing padding instruction.");
            Assert.That(access.Parameters[0].ParameterType, Is.TypeOf<Cpp2IL.Core.Model.Contexts.SzArrayTypeAnalysisContext>());
            Assert.That(((Cpp2IL.Core.Model.Contexts.SzArrayTypeAnalysisContext)access.Parameters[0].ParameterType).ElementType,
                Is.SameAs(elementSize == 8 ?
                    (unsigned ? app.SystemTypes.SystemUInt64Type : app.SystemTypes.SystemInt64Type) :
                    unsigned ? app.SystemTypes.SystemUInt32Type : app.SystemTypes.SystemInt32Type));
            Assert.That(X86CallerExceptionRegionProof.Check(access, native,
                calls.Select(instruction => instruction.IP).ToHashSet()), Is.Null);
            Assert.That(app.InstructionSet.GetIsilFromMethod(access).Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { IsilOpCode.Move, IsilOpCode.Return }));
            var wrongBranch = native.ToArray();
            wrongBranch[4].Code = Code.Ja_rel8_64;
            Assert.That(X86IntegerArrayAccessProof.TryProveShape(wrongBranch, isWrite, elementSize), Is.False);
            var wrongElement = native.ToArray();
            wrongElement[6].MemoryDisplacement64 = 0x24;
            Assert.That(X86IntegerArrayAccessProof.TryProveShape(wrongElement, isWrite, elementSize), Is.False);
            Assert.That(X86IntegerArrayAccessProof.TryProveShape(native, isWrite, elementSize == 8 ? 4 : 8), Is.False,
                "The same offset must not be interpreted with another element width.");
            if (isWrite)
            {
                var wrongSource = native.ToArray();
                wrongSource[6].Op1Register = Register.R9D;
                Assert.That(X86IntegerArrayAccessProof.TryProveShape(wrongSource, true, elementSize), Is.False);
            }
            var identity = X86RuntimeNullThrowProof.BindIdentity(app, "IndexOutOfRangeException")!;
            var originalAttributes = identity.Attributes;
            try
            {
                identity.Attributes |= MethodAttributes.Static;
                Assert.That(calls.All(instruction =>
                    !X86RuntimeBoundsThrowProof.TryIdentify(app, instruction.NearBranchTarget)), Is.True);
            }
            finally { identity.Attributes = originalAttributes; }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
