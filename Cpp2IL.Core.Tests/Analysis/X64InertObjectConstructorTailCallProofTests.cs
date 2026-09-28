using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Analysis;

[NonParallelizable]
public class X64InertObjectConstructorTailCallProofTests
{
    [Test]
    public void ExactPlayerBindsOnlyCompleteTypedLiteralStoreTails()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_SHARED_INERT_CTOR_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_SHARED_INERT_CTOR_FIXTURE_INPUT to the neutral exact player input.");

        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data",
            "il2cpp_data", "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata,
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var assembly = app.GetAssemblyByName(
                "SharedInertConstructorFixture")!;
            var objectConstructor = app.SystemTypes.SystemObjectType.Methods
                .Single(method => method.Name == ".ctor" &&
                    method.Parameters.Count == 0);
            var convention = app.InstructionSet.CallingConventionResolver!;

            foreach (var ownerName in new[] { "IntegerCell", "MixedCell" })
            {
                var owner = assembly.Types.Single(type =>
                    type.Name == ownerName);
                var constructor = owner.Methods.Single(method =>
                    method.Name == ".ctor");
                constructor.EnsureRawBytes();
                var native = X86Utils.Iterate(constructor).ToArray();
                Assert.That(native.Length, Is.GreaterThanOrEqualTo(4));
                Assert.That(native[0].Code, Is.EqualTo(Code.Xor_r32_rm32));
                Assert.That(native[^1].Code,
                    Is.EqualTo(Code.Jmp_rel32_64));
                var target = native[^1].NearBranchTarget;
                var aliases = app.MethodsByAddress[target];
                Assert.That(aliases, Does.Contain(objectConstructor));
                Assert.That(aliases.Count, Is.GreaterThan(1));
                var bufferedReturn = app.GetAssemblyByName("mscorlib")!
                    .GetTypeByFullName("System.Decimal")!;
                var unrelatedAlias = new InjectedMethodAnalysisContext(
                    app.SystemTypes.SystemInt32Type, "Buffered",
                    bufferedReturn, MethodAttributes.Public, []);
                Assert.That(convention.ReturnsViaHiddenBuffer(
                    unrelatedAlias), Is.True);
                aliases.Add(unrelatedAlias);
                try
                {
                    Assert.That(aliases.Any(convention.ReturnsViaHiddenBuffer),
                        Is.True, "an unrelated return-buffer alias triggers the guarded path");
                    constructor.Analyze();
                    var resolvedCall = constructor.ControlFlowGraph!.Instructions
                        .Single(instruction => instruction.IsCall &&
                            instruction.NativeAddress == native[^1].IP);
                    Assert.That(resolvedCall.Operands[0],
                        Is.SameAs(objectConstructor),
                        "the complete resolver must bind the real tail call");

                    var receiver = constructor.ParameterLocals[0];
                    Assert.That(receiver.IsThis, Is.True);
                    var call = new Cpp2IL.Core.ISIL.Instruction(0,
                        Cpp2IL.Core.ISIL.OpCode.CallVoid,
                        new Immediate(unchecked((long)target)))
                        { NativeAddress = native[^1].IP,
                          DeferredCallReturns = [],
                          RawCallStackArgumentCount = 0 };
                    call.AddOperands(convention.ResolveForUnmanaged(app, target));
                    call.SetOperand(1, receiver);
                    call.SetOperand(2, new Immediate(0));
                    Assert.That(convention.HasRawArgumentLayout(call, app),
                        Is.True);
                    Assert.That(X64InertObjectConstructorTailCallProof.Find(
                        constructor, call, target, aliases),
                        Is.SameAs(objectConstructor));

                    call.NativeAddress = native[0].IP;
                    Assert.That(X64InertObjectConstructorTailCallProof.Find(
                        constructor, call, target, aliases), Is.Null,
                        "a different callsite cannot inherit the tail proof");
                    call.NativeAddress = native[^1].IP;
                    call.SetOperand(2, new Immediate(1));
                    Assert.That(X64InertObjectConstructorTailCallProof.Find(
                        constructor, call, target, aliases), Is.Null,
                        "the hidden MethodInfo must be the native zero");
                    call.SetOperand(2, new Immediate(0));
                    receiver.IsThis = false;
                    Assert.That(X64InertObjectConstructorTailCallProof.Find(
                        constructor, call, target, aliases), Is.Null,
                        "an ordinary RCX value cannot stand in for this");
                    receiver.IsThis = true;

                    var falseReceiver = new LocalVariable("this",
                        new Cpp2IL.Core.ISIL.Register(null, "rcx"), owner)
                        { IsThis = true };
                    call.SetOperand(1, falseReceiver);
                    Assert.That(X64InertObjectConstructorTailCallProof.Find(
                        constructor, call, target, aliases), Is.Null,
                        "a fabricated IsThis local is not the caller's parameter");
                    call.SetOperand(1, receiver);

                    var firstField = owner.Fields.First(field =>
                        !field.IsStatic);
                    try
                    {
                        firstField.OverrideOffset = firstField.DefaultOffset + 1;
                        Assert.That(X64InertObjectConstructorTailCallProof.Find(
                            constructor, call, target, aliases), Is.Null,
                            "a native store must match its exact field offset");
                    }
                    finally { firstField.OverrideOffset = null; }
                    try
                    {
                        firstField.OverrideFieldType =
                            app.SystemTypes.SystemInt64Type;
                        Assert.That(X64InertObjectConstructorTailCallProof.Find(
                            constructor, call, target, aliases), Is.Null,
                            "a native store cannot change its field width");
                    }
                    finally { firstField.OverrideFieldType = null; }

                    var compatibleAlias = new InjectedMethodAnalysisContext(
                        owner, "Buffered", bufferedReturn,
                        MethodAttributes.Public, []);
                    Assert.That(convention.ReturnsViaHiddenBuffer(
                        compatibleAlias), Is.True);
                    aliases.Add(compatibleAlias);
                    try
                    {
                        Assert.That(
                            X64InertObjectConstructorTailCallProof.Find(
                                constructor, call, target, aliases), Is.Null,
                            "a compatible hidden-buffer alias must stay ambiguous");
                    }
                    finally { aliases.Remove(compatibleAlias); }

                    var selfConstructorAlias = new InjectedMethodAnalysisContext(
                        owner, ".ctor", app.SystemTypes.SystemVoidType,
                        MethodAttributes.Public, []);
                    aliases.Add(selfConstructorAlias);
                    try
                    {
                        Assert.That(X64InertObjectConstructorTailCallProof.Find(
                            constructor, call, target, aliases), Is.Null,
                            "a compatible own constructor at the shared pointer stays ambiguous");
                    }
                    finally { aliases.Remove(selfConstructorAlias); }
                }
                finally { aliases.Remove(unrelatedAlias); }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
