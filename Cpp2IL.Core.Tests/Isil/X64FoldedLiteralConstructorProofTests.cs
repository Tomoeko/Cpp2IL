using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64FoldedLiteralConstructorProofTests
{
    [Test]
    public void ExactPlayerBindsEachFoldedCallerAndRejectsAlteredLayoutOrBody()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_FOLDED_LITERAL_CONSTRUCTOR_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FOLDED_LITERAL_CONSTRUCTOR_FIXTURE_INPUT to the neutral player-input directory.");
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
            var types = app.GetAssemblyByName("FoldedLiteralConstructorFixture")!.Types;
            var first = types.Single(type => type.Name == "FirstCell");
            var second = types.Single(type => type.Name == "SecondCell");
            var third = types.Single(type => type.Name == "ThirdCell");
            var firstConstructor = first.Methods.Single(method => method.Name == ".ctor");
            var secondConstructor = second.Methods.Single(method => method.Name == ".ctor");
            var thirdConstructor = third.Methods.Single(method => method.Name == ".ctor");
            firstConstructor.EnsureRawBytes();
            secondConstructor.EnsureRawBytes();
            thirdConstructor.EnsureRawBytes();
            var firstNative = X86Utils.Iterate(firstConstructor).ToArray();
            var secondNative = X86Utils.Iterate(secondConstructor).ToArray();
            var thirdNative = X86Utils.Iterate(thirdConstructor).ToArray();
            var firstState = first.Fields.Single(field => field.Name == "State");
            var secondState = second.Fields.Single(field => field.Name == "State");
            var thirdState = third.Fields.Single(field => field.Name == "State");

            Assert.Multiple(() =>
            {
                Assert.That(firstConstructor.UnderlyingPointer,
                    Is.EqualTo(secondConstructor.UnderlyingPointer));
                Assert.That(firstConstructor.UnderlyingPointer,
                    Is.Not.EqualTo(thirdConstructor.UnderlyingPointer));
                Assert.That(app.MethodsByAddress[firstConstructor.UnderlyingPointer].Count,
                    Is.EqualTo(2));
                Assert.That(firstConstructor.RawBytes.Length, Is.EqualTo(14));
                Assert.That(thirdConstructor.RawBytes.Length, Is.GreaterThan(16));
                Assert.That(X64FoldedLiteralConstructorProof.TryProveShape(firstNative)?.Value,
                    Is.EqualTo(37));
                Assert.That(X64FoldedLiteralConstructorProof.TryProveShape(thirdNative)?.Value,
                    Is.EqualTo(-19));
                Assert.That(firstState.Offset, Is.EqualTo(24));
                Assert.That(secondState.Offset, Is.EqualTo(24));
                Assert.That(thirdState.Offset, Is.EqualTo(24));
            });

            Assert.That(X64FoldedLiteralConstructorProof.Find(firstConstructor, firstNative)?.Field,
                Is.SameAs(firstState));
            Assert.That(X64FoldedLiteralConstructorProof.Find(secondConstructor, secondNative)?.Field,
                Is.SameAs(secondState));
            Assert.That(X64FoldedLiteralConstructorProof.Find(thirdConstructor, thirdNative)?.Field,
                Is.SameAs(thirdState));
            Assert.That(X64FoldedLiteralConstructorProof.Find(firstConstructor, firstNative)?
                .BaseConstructor, Is.SameAs(app.SystemTypes.SystemObjectType.Methods
                    .Single(method => method.Name == ".ctor")));

            var wrongReceiver = firstNative.ToArray();
            wrongReceiver[1].MemoryBase = Register.RDX;
            Assert.That(X64FoldedLiteralConstructorProof.TryProveShape(wrongReceiver), Is.Null);
            var wrongWidth = firstNative.ToArray();
            wrongWidth[1].Code = Code.Mov_rm64_imm32;
            Assert.That(X64FoldedLiteralConstructorProof.TryProveShape(wrongWidth), Is.Null);
            var wrongExit = firstNative.ToArray();
            wrongExit[2].Code = Code.Call_rel32_64;
            Assert.That(X64FoldedLiteralConstructorProof.TryProveShape(wrongExit), Is.Null);
            Assert.That(X64FoldedLiteralConstructorProof.Find(firstConstructor,
                firstNative.Append(firstNative[2]).ToArray()), Is.Null);

            var guard = first.Fields.Single(field => field.Name == "Guard");
            try
            {
                guard.OverrideOffset = firstState.Offset;
                Assert.That(X64FoldedLiteralConstructorProof.Find(firstConstructor, firstNative),
                    Is.Null, "an overlapping sibling field invalidates the native store layout");
            }
            finally { guard.OverrideOffset = null; }

            try
            {
                firstState.OverrideFieldType = app.SystemTypes.SystemObjectType;
                Assert.That(X64FoldedLiteralConstructorProof.Find(firstConstructor, firstNative),
                    Is.Null, "the native dword store requires an unchanged Int32 field");
            }
            finally { firstState.OverrideFieldType = null; }

            var aliases = app.MethodsByAddress[firstConstructor.UnderlyingPointer];
            var binding = aliases.FindIndex(method => ReferenceEquals(method, firstConstructor));
            Assert.That(binding, Is.GreaterThanOrEqualTo(0));
            try
            {
                aliases.RemoveAt(binding);
                Assert.That(X64FoldedLiteralConstructorProof.Find(firstConstructor, firstNative),
                    Is.Null, "the current managed constructor must bind to its native entry");
            }
            finally { aliases.Insert(binding, firstConstructor); }

            try
            {
                firstConstructor.ImplAttributes |= MethodImplAttributes.InternalCall;
                Assert.That(X64FoldedLiteralConstructorProof.Find(firstConstructor, firstNative),
                    Is.Null);
            }
            finally { firstConstructor.ImplAttributes = firstConstructor.DefaultImplAttributes; }

            Assert.That(X64FoldedLiteralConstructorProof.Find(firstConstructor, firstNative),
                Is.Not.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    [Test]
    public void EstimatedSuffixNeedsExactTrapPaddingAndFreshUnwindEntry()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_FOLDED_LITERAL_CONSTRUCTOR_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FOLDED_LITERAL_CONSTRUCTOR_FIXTURE_INPUT to the neutral player-input directory.");
        var binaryPath = Path.Combine(directory!, "GameAssembly.dll");
        var metadataPath = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        var binary = File.ReadAllBytes(binaryPath);
        var metadata = File.ReadAllBytes(metadataPath);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var third = app.GetAssemblyByName("FoldedLiteralConstructorFixture")!.Types
                .Single(type => type.Name == "ThirdCell").Methods
                .Single(method => method.Name == ".ctor");
            third.EnsureRawBytes();
            var native = X86Utils.Iterate(third).ToArray();
            var end = third.UnderlyingPointer + 14;
            var next = end + 2;
            var unwind = X64UnwindProof.ForApplication(app)!;
            Assert.That(third.RawBytes.Length, Is.GreaterThan(16));
            Assert.That(unwind.ClassifySpan(third.UnderlyingPointer, next).Kind,
                Is.EqualTo(X64UnwindProof.SpanKind.NoEntry));
            Assert.That(unwind.HasFunctionEntryAt(next, native[5].NextIP), Is.True);
            Assert.That(X64FoldedLiteralConstructorProof.Find(third, native), Is.Not.Null);
            var padding = checked((int)((PE)app.Binary).MapVirtualAddressToRaw(end, false));
            Assert.That(binary[padding], Is.EqualTo(0xCC));

            var altered = (byte[])binary.Clone();
            altered[padding] = 0x90;
            Cpp2IlApi.ResetInternalState();
            Cpp2IlApi.InitializeLibCpp2Il(altered, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var changed = Cpp2IlApi.CurrentAppContext!
                .GetAssemblyByName("FoldedLiteralConstructorFixture")!.Types
                .Single(type => type.Name == "ThirdCell").Methods
                .Single(method => method.Name == ".ctor");
            changed.EnsureRawBytes();
            Assert.That(X64FoldedLiteralConstructorProof.Find(changed,
                X86Utils.Iterate(changed).ToArray()), Is.Null);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
