using System.Collections.Generic;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests.Analysis;

[NonParallelizable]
public class WordFieldEqualityProofTests
{
    private ApplicationAnalysisContext _app = null!;

    [OneTimeSetUp]
    public void LoadPublicTypeModel()
    {
        Cpp2IlApi.ResetInternalState();
        _app = TestGameLoader.LoadSimple2019Game();
    }

    [OneTimeTearDown]
    public void ReleasePublicTypeModel()
    {
        Cpp2IlApi.ResetInternalState();
    }

    [TestCase("short", OpCode.CheckEqual)]
    [TestCase("ushort", OpCode.CheckEqual)]
    [TestCase("char", OpCode.CheckEqual)]
    [TestCase("short", OpCode.CheckNotEqual)]
    [TestCase("ushort", OpCode.CheckNotEqual)]
    [TestCase("char", OpCode.CheckNotEqual)]
    public void ExactWordCaptureCanOnlyFeedTheSameWidthZeroPredicate(string kind, OpCode opcode)
    {
        var (instructions, _, reference) = GraphInstructions(kind);
        instructions[1].OpCode = opcode;
        Assert.That(NarrowFieldEqualityProof.HasExactStorageWidth(reference.Field.FieldType, 16), Is.True);
        Assert.That(() => NarrowFieldEqualityProof.Validate(new(instructions),
            (field, width) => ReferenceEquals(field, reference) && width == 16), Throws.Nothing);
        Assert.That(NarrowFieldEqualityProof.HasExactStorageWidth(reference.Field.FieldType, 8), Is.False);
        Assert.That(NarrowFieldEqualityProof.HasUnchangedFieldLayout(reference, 16), Is.False,
            "A synthetic declaration without native layout is not a production field-width proof.");
    }

    [TestCase(8, 16)]
    [TestCase(16, 8)]
    public void ComparisonCannotChangeTheWidthOfAnIndependentlyProvedCapture(int captureWidth, int comparisonWidth)
    {
        var (instructions, _, _) = GraphInstructions("ushort");
        instructions[0].IntegerBitWidth = captureWidth;
        instructions[1].IntegerBitWidth = comparisonWidth;
        Assert.That(() => NarrowFieldEqualityProof.Validate(new(instructions), (_, _) => true),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("same width"));
    }

    [TestCase("unproved-layout")]
    [TestCase("changed-type")]
    [TestCase("overwritten-capture")]
    [TestCase("addressed-capture")]
    [TestCase("nonzero-comparison")]
    [TestCase("ordering-comparison")]
    [TestCase("untyped-memory")]
    [TestCase("missing-width")]
    [TestCase("capture-after-use")]
    [TestCase("different-block")]
    public void WordProofRetainsStorageAndValueFlowFailures(string defect)
    {
        var (instructions, captured, reference) = GraphInstructions("short");
        var capture = instructions[0];
        var compare = instructions[1];
        switch (defect)
        {
            case "changed-type": captured.Type = _app.SystemTypes.SystemInt32Type; break;
            case "overwritten-capture": instructions.Insert(1, new(4, OpCode.Move, captured, new Immediate(0))); break;
            case "addressed-capture": instructions.Insert(1, new(4, OpCode.Move, new LocalVariable("address", new Register(4, "address")), new AddressOf(captured))); break;
            case "nonzero-comparison": compare.SetOperand(2, new Immediate(256)); break;
            case "ordering-comparison": compare.OpCode = OpCode.CheckLessUnsigned; break;
            case "untyped-memory": capture.SetOperand(1, new MemoryOperand(reference.Local, addend: reference.Offset)); break;
            case "missing-width": capture.IntegerBitWidth = 0; break;
            case "capture-after-use": instructions[0] = compare; instructions[1] = capture; break;
            case "different-block": instructions.Insert(1, new(4, OpCode.Jump, compare)); break;
        }
        Assert.That(() => NarrowFieldEqualityProof.Validate(new(instructions), (_, width) => width == 16 && defect != "unproved-layout"),
            Throws.TypeOf<DecompilerException>().With.Message.Contains("Native word"));
    }

    [TestCase(16, 2, 17, 1, true)] // the second byte is occupied by another field
    [TestCase(16, 2, 15, 2, true)] // another field ends inside the candidate
    [TestCase(16, 2, 16, 2, true)] // complete overlap
    [TestCase(16, 2, 15, 4, true)] // enclosing storage
    [TestCase(16, 2, 18, 1, false)] // immediately following field
    [TestCase(16, 2, 14, 2, false)] // immediately preceding field
    [TestCase(16, 1, 17, 1, false)] // adjacent byte fields remain valid
    public void NativeStorageOverlapChecksBothEnds(long start, long size, long otherStart, long otherSize, bool overlaps)
        => Assert.That(NarrowFieldEqualityProof.StorageRangesOverlap(start, size, otherStart, otherSize), Is.EqualTo(overlaps));

    [Test]
    public void WiderIntegerOrBooleanStorageDoesNotBecomeAWordField()
    {
        Assert.That(NarrowFieldEqualityProof.HasExactStorageWidth(_app.SystemTypes.SystemInt32Type, 16), Is.False);
        Assert.That(NarrowFieldEqualityProof.HasExactStorageWidth(_app.SystemTypes.SystemBooleanType, 16), Is.False);
        Assert.That(NarrowFieldEqualityProof.HasExactStorageWidth(_app.SystemTypes.SystemUInt16Type, 32), Is.False);
    }

    private (List<Instruction> Instructions, LocalVariable Captured, FieldReference Field) GraphInstructions(string kind)
    {
        var owner = new InjectedTypeAnalysisContext(_app.AssembliesByName["Assembly-CSharp"], "Synthetic", "WordFields",
            _app.SystemTypes.SystemObjectType, TypeAttributes.Public | TypeAttributes.Class);
        var type = kind == "short" ? _app.SystemTypes.SystemInt16Type
            : kind == "ushort" ? _app.SystemTypes.SystemUInt16Type : _app.SystemTypes.SystemCharType;
        var field = new InjectedFieldAnalysisContext("Value", type, FieldAttributes.Public, owner, 16);
        var receiver = new LocalVariable("receiver", new Register(0, "receiver"), owner);
        var reference = new FieldReference(field, receiver, 16);
        var captured = new LocalVariable("captured", new Register(1, "captured"), type);
        var result = new LocalVariable("result", new Register(2, "result"), _app.SystemTypes.SystemBooleanType);
        return ([
            new(0, OpCode.Move, captured, reference) { IntegerBitWidth = 16 },
            new(1, OpCode.CheckEqual, result, captured, new Immediate(0)) { IntegerBitWidth = 16 },
            new(2, OpCode.Return, result),
        ], captured, reference);
    }
}
