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
public class X64GuardedBoxedInt32ProofTests
{
    [Test]
    public void ExactPlayerBindsTheGuardAndRejectsAlteredIdentityOrOperands()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_GUARDED_BOXED_CAST_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_GUARDED_BOXED_CAST_FIXTURE_INPUT to the neutral player input.");
        var binary = Path.Combine(directory, "GameAssembly.dll");
        var metadata = Path.Combine(directory, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.GetAssemblyByName("GuardedBoxedCastFixture")!.Types
                .Single(type => type.Name == "GuardedCast").Methods
                .Single(candidate => candidate.Name == "Matches");
            var unwind = X64UnwindProof.ForApplication(app)!;
            var region = unwind.ClassifySpan(method.UnderlyingPointer,
                method.UnderlyingPointer + 1);
            var decoded = X86Utils.Iterate(method).TakeWhile(instruction =>
                instruction.IP < region.End).ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(region.Kind,
                    Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
                Assert.That(region.Start, Is.EqualTo(method.UnderlyingPointer));
                Assert.That(region.End - region.Start, Is.EqualTo(125));
                Assert.That(unwind.MatchesUnwind(region.Start, region.End, 10, 0,
                    [0x0A, 0x34, 0x06, 0x00, 0x0A, 0x32, 0x06, 0x70]), Is.True);
                Assert.That(decoded.Length, Is.EqualTo(37));
                Assert.That(decoded[^1].Code, Is.EqualTo(Code.Int3));
                Assert.That(decoded[^1].NextIP, Is.EqualTo(region.End));
            });

            var body = decoded.Take(36).ToArray();
            var shape = X64GuardedBoxedInt32Proof.TryProveShape(body);
            Assert.That(shape, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(shape!.TypeInfoSlot, Is.EqualTo(body[14].IPRelativeMemoryAddress));
                Assert.That(shape.UnboxExport,
                    Is.EqualTo(((PE)app.Binary).GetVirtualAddressOfExportedFunctionByName(
                        "il2cpp_object_unbox")));
                Assert.That(X64GuardedBoxedInt32Proof.BindProvedShape(method, shape),
                    Is.True);
                Assert.That(X64GuardedBoxedInt32Proof.Find(method), Is.True);
            });

            foreach (var changed in new[]
                     {
                         shape! with { TypeInfoSlot = shape.OnceFlag },
                         shape! with { OnceFlag = shape.TypeInfoSlot },
                         shape! with { MetadataInitializer = shape.UnboxExport },
                         shape! with { UnboxExport = shape.ColdCall },
                         shape! with { ColdCall = method.UnderlyingPointer }
                     })
                Assert.That(X64GuardedBoxedInt32Proof.BindProvedShape(method,
                    changed), Is.False);

            foreach (var mutation in new[]
                     {
                         "metadata branch", "object class base", "indexed class load",
                         "class equality", "guarded receiver", "type slot",
                         "indexed element load", "element offset", "receiver transfer",
                         "cold branch", "unbox target", "indexed payload",
                         "expected value", "false return", "intervening call",
                         "extra instruction"
                     })
            {
                var changed = body.ToArray();
                var index = mutation switch
                {
                    "metadata branch" => 6,
                    "object class base" or "indexed class load" => 12,
                    "class equality" => 15,
                    "guarded receiver" => 16,
                    "type slot" => 14,
                    "indexed element load" => 19,
                    "element offset" or "intervening call" => 20,
                    "receiver transfer" => 21,
                    "cold branch" => 22,
                    "unbox target" => 23,
                    "indexed payload" or "expected value" => 24,
                    "false return" => 31,
                    _ => 35
                };
                var instruction = changed[index];
                switch (mutation)
                {
                    case "metadata branch":
                        instruction.NearBranch64 = body[8].IP; break;
                    case "object class base":
                        instruction.MemoryBase = Register.RDX; break;
                    case "indexed class load":
                        instruction.MemoryIndex = Register.RAX; break;
                    case "class equality":
                        instruction.Op1Register = Register.RBX; break;
                    case "guarded receiver":
                        instruction.Op1Register = Register.RCX; break;
                    case "type slot":
                        instruction.MemoryDisplacement64 = body[3].IPRelativeMemoryAddress;
                        break;
                    case "indexed element load":
                        instruction.MemoryIndex = Register.RCX; break;
                    case "element offset":
                        instruction.MemoryDisplacement64++; break;
                    case "receiver transfer":
                        instruction.Op1Register = Register.RAX; break;
                    case "cold branch":
                        instruction.NearBranch64 = body[23].IP; break;
                    case "unbox target":
                        instruction.Code = Code.Jmp_rel32_64; break;
                    case "indexed payload":
                        instruction.MemoryIndex = Register.RCX; break;
                    case "expected value":
                        instruction.Op1Register = Register.EDX; break;
                    case "false return":
                        instruction.Op1Register = Register.CL; break;
                    case "intervening call":
                        instruction.Code = Code.Call_rel32_64; break;
                    case "extra instruction":
                        changed = [..changed, body[35]]; break;
                }
                if (mutation != "extra instruction")
                    changed[index] = instruction;
                Assert.That(X64GuardedBoxedInt32Proof.TryProveShape(changed),
                    Is.Null, mutation);
            }

            var aliases = app.MethodsByAddress[method.UnderlyingPointer];
            aliases.Add(method);
            try
            {
                Assert.That(X64GuardedBoxedInt32Proof.Find(method), Is.False,
                    "A shared native address cannot select one managed body.");
            }
            finally { aliases.RemoveAt(aliases.Count - 1); }

            var int32 = app.SystemTypes.SystemInt32Type;
            var originalNamespace = int32.OverrideNamespace;
            try
            {
                int32.OverrideNamespace = "Changed";
                Assert.That(X64GuardedBoxedInt32Proof.BindProvedShape(method,
                    shape!), Is.False);
            }
            finally { int32.OverrideNamespace = originalNamespace; }

            var originalAttributes = int32.OverrideAttributes;
            try
            {
                int32.OverrideAttributes = int32.Attributes & ~TypeAttributes.Sealed;
                Assert.That(X64GuardedBoxedInt32Proof.BindProvedShape(method,
                    shape!), Is.False);
            }
            finally { int32.OverrideAttributes = originalAttributes; }

            Assert.That(X64GuardedBoxedInt32Proof.Find(method), Is.True);
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
