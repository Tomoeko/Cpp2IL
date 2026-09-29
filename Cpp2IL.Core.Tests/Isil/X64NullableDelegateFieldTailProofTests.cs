using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.Tests.Isil;

public class X64NullableDelegateFieldTailProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerRequiresSingleCapturedDelegateAndUnchangedMetadata()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_NULLABLE_DELEGATE_FIELD_TAIL_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_NULLABLE_DELEGATE_FIELD_TAIL_FIXTURE_INPUT to the neutral player-input directory.");
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
            var types = app.GetAssemblyByName("NullableDelegateFieldTailFixture")!.Types;
            foreach (var ownerName in new[] { "DirectSignalNode", "PaddedSignalNode" })
            {
                var owner = types.Single(type => type.Name == ownerName);
                var method = owner.Methods.Single(candidate =>
                    candidate.Name == "FireIfPresent");
                method.EnsureRawBytes();
                var native = X86Utils.Iterate(method).ToArray();
                Assert.That(native, Has.Length.EqualTo(7));
                Assert.That(app.MethodsByAddress[method.UnderlyingPointer],
                    Is.EqualTo(new[] { method }));
                Assert.That(X86CallerExceptionRegionProof.Check(method, native,
                    new HashSet<ulong>()), Does.Contain("unproved exit"));
                Assert.That(X64NullableDelegateFieldTailProof.TryProveShape(native),
                    Is.Not.Null);
                Assert.That(
                    X86CallerExceptionRegionProof.CheckProvedGuardedTerminalIndirectBranch(
                        method, native, native[5].IP, native[6].IP), Is.Null);

                var evidence = X64NullableDelegateFieldTailProof.Find(method, native);
                Assert.That(evidence, Is.Not.Null, ownerName);
                Assert.That(evidence!.Field.Name, Is.EqualTo("Callback"));
                Assert.That(evidence.Invoke.Name, Is.EqualTo("Invoke"));
                Assert.That(evidence.Field.FieldType, Is.SameAs(
                    evidence.Invoke.DeclaringType));
                Assert.That(evidence.Field.BackingData?.Field.RawFieldType?.Type,
                    Is.EqualTo(Il2CppTypeEnum.IL2CPP_TYPE_CLASS));

                var wrongLoad = native.ToArray();
                wrongLoad[0].MemoryBase = Register.RDX;
                Assert.That(X64NullableDelegateFieldTailProof.TryProveShape(wrongLoad),
                    Is.Null);
                Assert.That(X64NullableDelegateFieldTailProof.Find(method, wrongLoad),
                    Is.Null, "the field must be read from the original receiver");
                var wrongTest = native.ToArray();
                wrongTest[1].Op1Register = Register.RCX;
                Assert.That(X64NullableDelegateFieldTailProof.TryProveShape(wrongTest),
                    Is.Null);
                Assert.That(X64NullableDelegateFieldTailProof.Find(method, wrongTest),
                    Is.Null, "the branch must test the captured delegate");
                var wrongBranch = native.ToArray();
                wrongBranch[2].NearBranch64 = native[5].IP;
                Assert.That(X64NullableDelegateFieldTailProof.TryProveShape(wrongBranch),
                    Is.Null);
                Assert.That(X64NullableDelegateFieldTailProof.Find(method, wrongBranch),
                    Is.Null, "null must reach the plain return");
                var wrongMethod = native.ToArray();
                wrongMethod[3].MemoryDisplacement64 = 0x20;
                Assert.That(X64NullableDelegateFieldTailProof.TryProveShape(wrongMethod),
                    Is.Null);
                Assert.That(X64NullableDelegateFieldTailProof.Find(method, wrongMethod),
                    Is.Null, "the MethodInfo slot is part of the delegate ABI");
                var wrongThis = native.ToArray();
                wrongThis[4].MemoryDisplacement64 = 0x38;
                Assert.That(X64NullableDelegateFieldTailProof.TryProveShape(wrongThis),
                    Is.Null);
                Assert.That(X64NullableDelegateFieldTailProof.Find(method, wrongThis),
                    Is.Null, "the invoke receiver slot is part of the delegate ABI");
                var wrongTail = native.ToArray();
                wrongTail[5].MemoryDisplacement64 = 0x10;
                Assert.That(X64NullableDelegateFieldTailProof.TryProveShape(wrongTail),
                    Is.Null);
                Assert.That(X64NullableDelegateFieldTailProof.Find(method, wrongTail),
                    Is.Null, "the tail must use the delegate invoke implementation");
                Assert.That(
                    X86CallerExceptionRegionProof.CheckProvedGuardedTerminalIndirectBranch(
                        method, native, native[4].IP, native[6].IP),
                    Does.Contain("no proved return path"),
                    "the special exit must be the actual terminal jump");

                var field = evidence.Field;
                try
                {
                    field.OverrideOffset = field.DefaultOffset + 8;
                    Assert.That(X64NullableDelegateFieldTailProof.Find(method, native),
                        Is.Null, "the raw field offset must remain unchanged");
                }
                finally { field.OverrideOffset = null; }
                try
                {
                    field.OverrideFieldType = app.SystemTypes.SystemStringType;
                    Assert.That(X64NullableDelegateFieldTailProof.Find(method, native),
                        Is.Null, "an ordinary reference cannot be invoked as a delegate");
                }
                finally { field.OverrideFieldType = null; }
                var neighbor = owner.Fields.Single(candidate =>
                    candidate.Name == "Neighbor");
                try
                {
                    neighbor.OverrideOffset = field.Offset;
                    Assert.That(X64NullableDelegateFieldTailProof.Find(method, native),
                        Is.Null, "an overlapping sibling invalidates field identity");
                }
                finally { neighbor.OverrideOffset = null; }
                var rawField = field.BackingData!.Field;
                var oldTypeIndex = rawField.typeIndex;
                try
                {
                    rawField.typeIndex = neighbor.BackingData!.Field.typeIndex;
                    Assert.That(X64NullableDelegateFieldTailProof.Find(method, native),
                        Is.Null, "the raw field must remain a delegate class reference");
                }
                finally { rawField.typeIndex = oldTypeIndex; }

                var oldReturn = method.OverrideReturnType;
                try
                {
                    method.OverrideReturnType = app.SystemTypes.SystemInt32Type;
                    Assert.That(X64NullableDelegateFieldTailProof.Find(method, native),
                        Is.Null, "the caller's return ABI must remain void");
                }
                finally { method.OverrideReturnType = oldReturn; }
                var delegateType = field.FieldType;
                var oldBase = delegateType.OverrideBaseType;
                try
                {
                    delegateType.OverrideBaseType = app.SystemTypes.SystemObjectType;
                    Assert.That(X64NullableDelegateFieldTailProof.Find(method, native),
                        Is.Null, "the field type must remain a delegate");
                }
                finally { delegateType.OverrideBaseType = oldBase; }
                var invoke = evidence.Invoke;
                var oldImplementation = invoke.ImplAttributes;
                try
                {
                    invoke.ImplAttributes = MethodImplAttributes.IL;
                    Assert.That(X64NullableDelegateFieldTailProof.Find(method, native),
                        Is.Null, "delegate Invoke must retain its runtime implementation");
                }
                finally { invoke.ImplAttributes = oldImplementation; }
                var oldParameterCount = invoke.Definition!.parameterCount;
                try
                {
                    invoke.Definition.parameterCount = 1;
                    Assert.That(X64NullableDelegateFieldTailProof.Find(method, native),
                        Is.Null, "native argument registers only prove zero arguments");
                }
                finally { invoke.Definition.parameterCount = oldParameterCount; }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
