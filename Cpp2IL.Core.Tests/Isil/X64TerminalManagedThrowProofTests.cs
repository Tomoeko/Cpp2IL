using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64TerminalManagedThrowProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerProvesClosedThrowAndRejectsReturningOrAlteredTargets()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_THROW_ONLY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_THROW_ONLY_FIXTURE_INPUT to the neutral synthetic player-input directory.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata",
            "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var owner = app.GetAssemblyByName("ThrowOnlyFixture")!.Types
                .Single(type => type.Name == "ThrowOnlyCases");
            var returning = owner.Methods.Single(method => method.Name == "ReturnAfterCall");
            returning.EnsureRawBytes();
            Assert.That(X64TerminalManagedThrowProof.Find(returning,
                X86Utils.Iterate(returning).ToArray()), Is.Null,
                "a returning call with later field effects is outside the throw proof");

            foreach (var name in new[] { "ThrowNotSupported", "ThrowNotImplemented" })
            {
                var method = owner.Methods.Single(candidate => candidate.Name == name);
                method.EnsureRawBytes();
                var native = X86Utils.Iterate(method).ToArray();
                Assert.Multiple(() =>
                {
                    Assert.That(native, Has.Length.EqualTo(17));
                    Assert.That(method.RawBytes.Length, Is.EqualTo(70));
                    Assert.That(X64TerminalManagedThrowProof.TryProveCallerShape(native), Is.True);
                    Assert.That(X86CallerExceptionRegionProof.Check(method, native,
                        new HashSet<ulong> { native[16].IP }), Is.Null);
                });

                var pe = (PE)app.Binary;
                var unwind = X64UnwindProof.ForApplication(app)!;
                var region = unwind.ClassifySpan(method.UnderlyingPointer, native[^1].NextIP);
                Assert.Multiple(() =>
                {
                    Assert.That(region.Kind, Is.EqualTo(X64UnwindProof.SpanKind.HandlerFree));
                    Assert.That(region.Start, Is.EqualTo(method.UnderlyingPointer));
                    Assert.That(region.RootStart, Is.EqualTo(method.UnderlyingPointer));
                    Assert.That(region.End - native[^1].NextIP, Is.InRange(0UL, 15UL));
                    Assert.That(unwind.MatchesUnwind(region.Start, region.End, 6, 0,
                        new byte[] { 0x06, 0x32, 0x02, 0x30 }), Is.True);
                    Assert.That(native[3].NearBranchTarget,
                        Is.EqualTo(app.GetOrCreateKeyFunctionAddresses()
                            .il2cpp_codegen_initialize_runtime_metadata));
                    Assert.That(X64MetadataInitializationHelperProof.TryIdentify(app, pe, unwind,
                        native[3].NearBranchTarget), Is.True);
                    Assert.That(X64IteratorAllocatorProof.IsAllocator(app,
                        native[5].NearBranchTarget), Is.True);
                });
                Assert.That(X64TerminalManagedThrowProof.ProveRaiseWrapper(app, pe, unwind,
                    native[16].NearBranchTarget), Is.True, "raise wrapper");
                Assert.That(X64TerminalManagedThrowProof.ProveRaiseWrapper(app, pe, unwind,
                    returning.UnderlyingPointer), Is.False,
                    "a returning managed call is not the runtime raise wrapper");

                var proof = X64TerminalManagedThrowProof.Find(method, native);
                Assert.That(proof, Is.Not.Null, name);
                Assert.That(proof!.ExceptionType.Name,
                    Is.EqualTo(name == "ThrowNotSupported" ? "NotSupportedException" :
                        "NotImplementedException"));
                Assert.That(proof.Constructor.Name, Is.EqualTo(".ctor"));
                var typeSlot = native[2].IPRelativeMemoryAddress;
                var methodSlot = native[12].IPRelativeMemoryAddress;
                var constructorTarget = native[11].NearBranchTarget;
                Assert.Multiple(() =>
                {
                    Assert.That(X64TerminalManagedThrowProof.BindMetadata(method, pe, unwind,
                        typeSlot, methodSlot, constructorTarget), Is.Not.Null);
                    Assert.That(X64TerminalManagedThrowProof.BindMetadata(method, pe, unwind,
                        methodSlot, methodSlot, constructorTarget), Is.Null,
                        "MethodDef cannot replace the exception TypeInfo slot");
                    Assert.That(X64TerminalManagedThrowProof.BindMetadata(method, pe, unwind,
                        typeSlot, typeSlot, constructorTarget), Is.Null,
                        "TypeInfo cannot replace the caller MethodDef slot");
                    Assert.That(X64TerminalManagedThrowProof.BindMetadata(method, pe, unwind,
                        typeSlot, methodSlot, returning.UnderlyingPointer), Is.Null,
                        "an unrelated managed call cannot serve as the exception constructor");
                });
                var constructorBindings = app.MethodsByAddress[constructorTarget];
                constructorBindings.Add(returning);
                try
                {
                    Assert.That(X64TerminalManagedThrowProof.BindMetadata(method, pe, unwind,
                        typeSlot, methodSlot, constructorTarget), Is.Null,
                        "an aliased constructor address cannot identify a unique constructor");
                }
                finally { constructorBindings.Remove(returning); }
                var lifted = X64TerminalManagedThrowProof.TryLift(method, native);
                Assert.That(lifted, Is.Not.Null);
                Assert.Multiple(() =>
                {
                    Assert.That(lifted!.Select(instruction => instruction.OpCode),
                        Is.EqualTo(new[] { ISIL.OpCode.Newobj, ISIL.OpCode.CallVoid, ISIL.OpCode.Throw }));
                    Assert.That(lifted[0].Operands[1], Is.SameAs(proof.ExceptionType));
                    Assert.That(lifted[1].Operands[0], Is.SameAs(proof.Constructor));
                    Assert.That(lifted[0].Destination, Is.EqualTo(lifted[1].Operands[1]));
                    Assert.That(lifted[1].Operands[1], Is.EqualTo(lifted[2].Operands[0]));
                });

                var returningTarget = native.ToArray();
                returningTarget[16].NearBranch64 = returning.UnderlyingPointer;
                Assert.That(X64TerminalManagedThrowProof.Find(method, returningTarget), Is.Null,
                    "altered decoded bytes cannot authenticate the caller");
                var missingArg = native.ToArray();
                missingArg[15].Code = Code.Nopd;
                Assert.That(X64TerminalManagedThrowProof.TryProveCallerShape(missingArg), Is.False,
                    "the exception object must reach the raise wrapper");
                var wrongWidth = native.ToArray();
                wrongWidth[14].Code = Code.Mov_r32_rm32;
                Assert.That(X64TerminalManagedThrowProof.TryProveCallerShape(wrongWidth), Is.False);
                var extraEffect = native.ToArray();
                extraEffect[9].Code = Code.Inc_rm32;
                Assert.That(X64TerminalManagedThrowProof.TryProveCallerShape(extraEffect), Is.False);

                var callerBindings = method.AppContext.MethodsByAddress[method.UnderlyingPointer];
                callerBindings.Add(returning);
                try
                {
                    Assert.That(X64TerminalManagedThrowProof.Find(method, native), Is.Null,
                        "a shared native address cannot uniquely prove this method");
                }
                finally { callerBindings.Remove(returning); }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
