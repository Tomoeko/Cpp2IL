using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

public class X86RuntimeNullThrowProofTests
{
    [Test]
    public void RecognizesTheExactRuntimeNullOperationWithoutClaimingOrdinaryConstruction()
        => Assert.That(new NativeFixture().Proves(), Is.True);

    [TestCase("il2cpp_get_corlib")]
    [TestCase("il2cpp_class_from_name")]
    [TestCase("il2cpp_object_new")]
    [TestCase("il2cpp_runtime_object_init")]
    [TestCase("il2cpp_raise_exception")]
    public void EveryExportAnchorIsRequired(string export)
    {
        var fixture = new NativeFixture();
        fixture.Exports.Remove(export);
        Assert.That(fixture.Proves(), Is.False);
    }

    [TestCase(0x1000UL, 0, "4883EC20")] // incorrect shadow-space shape
    [TestCase(0x1000UL, 1, "7500")] // conditional reachability is not unconditional throw
    [TestCase(0x1000UL, 1, "FF11")] // indirect call
    [TestCase(0x1100UL, 1, "488D542420")] // empty view passed in the wrong argument
    [TestCase(0x1100UL, 1, "488D4C2428")] // unproved stack slice
    [TestCase(0x1100UL, 3, "488BCB")] // ignores the initialized view result
    [TestCase(0x1200UL, 0, "B801000000")] // a nonempty/unknown view cannot skip message effects
    [TestCase(0x1200UL, 1, "8901")] // only a half-width pointer store
    [TestCase(0x1200UL, 2, "48894110")] // does not initialize the length slot
    [TestCase(0x1200UL, 3, "488BC2")] // initializer returns a different pointer
    [TestCase(0x1200UL, 4, "C20000")] // different stack cleanup ABI
    [TestCase(0x1300UL, 2, "488BCB")] // throws a different object
    [TestCase(0x1300UL, 3, "33C9")] // destroys exception argument
    [TestCase(0x1300UL, 3, "BA01000000")] // supplies a non-null last managed frame
    [TestCase(0x1300UL, 4, "E800000000")] // reaches an unanchored target
    [TestCase(0x1400UL, 0, "488919")] // externally visible store before construction
    [TestCase(0x1400UL, 3, "488BFA")] // captures an uninitialized incoming argument
    [TestCase(0x1400UL, 6, "488BCB")] // wrong image into class lookup
    [TestCase(0x1400UL, 8, "E800000000")] // unanchored class lookup
    [TestCase(0x1400UL, 9, "488BCB")] // allocation consumes a different class
    [TestCase(0x1400UL, 10, "E800000000")] // unanchored allocator
    [TestCase(0x1400UL, 11, "488BCF")] // constructor receives the message buffer
    [TestCase(0x1400UL, 12, "488BDA")] // preserves a different object as the result
    [TestCase(0x1400UL, 13, "E800000000")] // unknown initializer effect
    [TestCase(0x1400UL, 14, "48837F0801")] // zero length no longer establishes branch outcome
    [TestCase(0x1400UL, 14, "48833F00")] // compares a different member of the view
    [TestCase(0x1400UL, 15, "0F8200000000")] // JB would run the message write on equality
    [TestCase(0x2000UL, 0, "E800000000")] // a call is not a closed export tail thunk
    [TestCase(0x2300UL, 0, "488919")] // allocator wrapper has an extra visible effect
    [TestCase(0x2400UL, 1, "33C9")] // export does not preserve the exception argument
    public void RejectsMissingDataflowWidthEffectsOrTermination(ulong address, int index, string replacement)
    {
        var fixture = new NativeFixture();
        fixture.ReplaceInstruction(address, index, replacement);
        Assert.That(fixture.Proves(), Is.False);
    }

    [TestCase("OtherException", "System")]
    [TestCase("NullReferenceException", "Other")]
    [TestCase("NullReferenceExceptionSuffix", "System")]
    public void ExactTypeOperandsAreRequiredRatherThanAnExceptionNameHint(string type, string ns)
    {
        var fixture = new NativeFixture();
        fixture.Strings[0x4000] = type;
        fixture.Strings[0x4100] = ns;
        Assert.That(fixture.Proves(), Is.False);
    }

    [TestCase("F3")]
    [TestCase("F2")]
    [TestCase("64")]
    [TestCase("F0")]
    public void RejectsPrefixedWrapperInstructions(string prefix)
    {
        var fixture = new NativeFixture();
        fixture.ReplaceInstruction(0x1000, 0, prefix + "4883EC28");
        Assert.That(fixture.Proves(), Is.False);
    }

    [Test]
    public void DoesNotInferConstructionFromAnExistingRaiserOrArbitraryCallChain()
    {
        var fixture = new NativeFixture();
        Assert.That(fixture.Proves(0x2400), Is.False);
        fixture.Exports["il2cpp_runtime_object_init"] = fixture.Exports["il2cpp_get_corlib"];
        Assert.That(fixture.Proves(), Is.False);
    }

    [Test]
    public void RejectsBrokenDecodeProvenanceAndTruncatedBodies()
    {
        var fixture = new NativeFixture();
        Assert.That(X86RuntimeNullThrowProof.TryProve(0x1000, (address, count) =>
        {
            var body = fixture.Read(address, count)?.ToArray();
            if (body != null && address == 0x1200)
            {
                var instruction = body[1];
                instruction.IP++;
                body[1] = instruction;
            }
            return body;
        }, fixture.Export, fixture.ReadString, _ => true), Is.False);
        fixture.Bodies[0x1200] = fixture.Bodies[0x1200][..^1];
        Assert.That(fixture.Proves(), Is.False);
    }

    [Test]
    public void UnreachableBytesAfterTheProvedNoreturnCallDoNotInventAContinuation()
    {
        var fixture = new NativeFixture();
        fixture.Bodies[0x1000] = [.. fixture.Bodies[0x1000], 0x48, 0x89, 0x01, 0xC3];
        Assert.That(fixture.Proves(), Is.True);
    }

    [TestCase(0x1000UL)]
    [TestCase(0x1100UL)]
    [TestCase(0x1200UL)]
    [TestCase(0x1300UL)]
    [TestCase(0x1400UL)]
    public void EveryExecutedCandidateFrameRequiresIndependentUnwindEvidence(ulong rejected)
    {
        var fixture = new NativeFixture();
        fixture.Unwind = regions =>
        {
            Assert.That(regions.Select(region => region.Start), Is.EqualTo(new ulong[] { 0x1000, 0x1100, 0x1200, 0x1300, 0x1400 }));
            Assert.That(regions.Where(region => region.Prolog == X86RuntimeNullThrowProof.Frame.Leaf).Select(region => region.Start),
                Is.EqualTo(new ulong[] { 0x1200 }));
            Assert.That(regions.All(region => region.End > region.Start), Is.True);
            return regions.All(region => region.Start != rejected);
        };
        Assert.That(fixture.Proves(), Is.False);
    }

    private sealed class NativeFixture
    {
        public readonly Dictionary<ulong, byte[]> Bodies = [];
        public readonly Dictionary<string, ulong> Exports = new()
        {
            ["il2cpp_get_corlib"] = 0x2000, ["il2cpp_class_from_name"] = 0x2100,
            ["il2cpp_runtime_object_init"] = 0x2200, ["il2cpp_object_new"] = 0x2300,
            ["il2cpp_raise_exception"] = 0x2400,
        };
        public readonly Dictionary<ulong, string> Strings = new() { [0x4000] = "NullReferenceException", [0x4100] = "System" };

        public NativeFixture()
        {
            Add(0x1000, b => b.Hex("4883EC28").Call(0x1100));
            Add(0x1100, b => b.Hex("4883EC38488D4C2420").Call(0x1200).Hex("488BC8").Call(0x1300));
            Add(0x1200, b => b.Hex("33C048890148894108488BC1C3"));
            Add(0x1300, b => b.Hex("4883EC28").Call(0x1400).Hex("488BC833D2").Call(0x3400));
            Add(0x1400, b =>
            {
                b.Hex("48895C2408574883EC20488BF9").Call(0x3000).Relative("4C8D05", 0x4000)
                    .Hex("488BC8").Relative("488D15", 0x4100).Call(0x3100).Hex("488BC8").Call(0x3300)
                    .Hex("488BC8488BD8").Call(0x3200).Hex("48837F0800");
                b.Relative("0F86", b.Address + 6 + 20).Hex(new string('C', 40)); // unreachable message arm
                b.Hex("488BC3488B5C24304883C4205FC3");
                return b;
            });
            Add(0x2000, b => b.Relative("E9", 0x3000));
            Add(0x2100, b => b.Relative("E9", 0x3100));
            Add(0x2200, b => b.Relative("E9", 0x3200));
            Add(0x2300, b => b.Hex("4883EC28").Call(0x3300).Hex("EB0233C04883C428C3"));
            Add(0x2400, b => b.Hex("4883EC2833D2").Call(0x3400));
        }

        public Func<IReadOnlyList<X86RuntimeNullThrowProof.Region>, bool> Unwind = _ => true;
        public bool Proves(ulong address = 0x1000) => X86RuntimeNullThrowProof.TryProve(address, Read, Export, ReadString, Unwind);
        public ulong Export(string name) => Exports.GetValueOrDefault(name);
        public string? ReadString(ulong address) => Strings.GetValueOrDefault(address);

        public IReadOnlyList<Instruction>? Read(ulong address, int count)
        {
            var body = Bodies.SingleOrDefault(pair => address >= pair.Key && address - pair.Key < (ulong)pair.Value.Length);
            if (body.Value == null)
                return null;
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(body.Value[(int)(address - body.Key)..]), address);
            var result = new Instruction[count];
            for (var i = 0; i < count; i++)
                result[i] = decoder.Decode();
            return result;
        }

        public void ReplaceInstruction(ulong address, int index, string replacement)
        {
            var instruction = Read(address, index + 1)![index];
            var offset = (int)(instruction.IP - address);
            var bytes = Bodies[address];
            Bodies[address] = [.. bytes[..offset], .. Convert.FromHexString(replacement), .. bytes[(offset + instruction.Length)..]];
        }

        private void Add(ulong address, Func<CodeBuilder, CodeBuilder> build) => Bodies[address] = build(new(address)).Bytes.ToArray();
    }

    private sealed class CodeBuilder(ulong start)
    {
        public readonly List<byte> Bytes = [];
        public ulong Address => start + (ulong)Bytes.Count;
        public CodeBuilder Hex(string bytes) { Bytes.AddRange(Convert.FromHexString(bytes)); return this; }
        public CodeBuilder Call(ulong target) => Relative("E8", target);
        public CodeBuilder Relative(string opcode, ulong target)
        {
            Hex(opcode);
            Bytes.AddRange(BitConverter.GetBytes(checked((int)((long)target - (long)Address - 4))));
            return this;
        }
    }
}
