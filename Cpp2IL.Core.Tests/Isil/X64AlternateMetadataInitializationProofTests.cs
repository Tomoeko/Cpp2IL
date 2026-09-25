using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests.Isil;

public class X64AlternateMetadataInitializationProofTests
{
    [Test]
    [NonParallelizable]
    public void AlternateRuntimeLayoutProvesTheMethodDefThrowRoute()
    {
        var binary = Environment.GetEnvironmentVariable("CPP2IL_ALTERNATE_METADATA_BINARY");
        var metadata = Environment.GetEnvironmentVariable("CPP2IL_ALTERNATE_METADATA");
        var addressText = Environment.GetEnvironmentVariable("CPP2IL_ALTERNATE_METADATA_METHOD_ADDRESS");
        if (string.IsNullOrWhiteSpace(binary) || string.IsNullOrWhiteSpace(metadata) ||
            string.IsNullOrWhiteSpace(addressText))
            Assert.Ignore("Set the alternate metadata player input and method address variables.");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        var address = Convert.ToUInt64(addressText, 16);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary!, metadata!,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var method = app.MethodsByAddress[address].Single();
            method.EnsureRawBytes();
            var native = X86Utils.Iterate(method).ToArray();
            var pe = (PE)app.Binary;
            var unwind = X64UnwindProof.ForApplication(app)!;
            var helper = native[3].NearBranchTarget;

            Assert.Multiple(() =>
            {
                Assert.That(X64TerminalManagedThrowProof.TryProveCallerShape(native.Take(17).ToArray()),
                    Is.True);
                Assert.That(X64MetadataInitializationHelperProof.TryIdentify(app, pe, unwind, helper),
                    Is.False, "the original native layout must stay independently qualified");
                Assert.That(X64MetadataInitializationHelperProof.TryIdentifyMethodDefArm(
                    app, pe, unwind, helper), Is.True);
                Assert.That(X86CallerExceptionRegionProof.Check(method, native,
                    new HashSet<ulong> { native[16].IP }), Is.Null);
            });
            Assert.That(X64TerminalManagedThrowProof.Find(method, native), Is.Not.Null);
            Assert.That(X64TerminalManagedThrowProof.TryLift(method, native)!
                .Select(instruction => instruction.OpCode),
                Is.EqualTo(new[] { ISIL.OpCode.Newobj, ISIL.OpCode.CallVoid, ISIL.OpCode.Throw }));

            var changedHelper = native.ToArray();
            changedHelper[13].NearBranch64 = method.UnderlyingPointer;
            Assert.That(X64TerminalManagedThrowProof.Find(method, changedHelper), Is.Null,
                "a changed metadata call cannot reuse the original native proof");
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
