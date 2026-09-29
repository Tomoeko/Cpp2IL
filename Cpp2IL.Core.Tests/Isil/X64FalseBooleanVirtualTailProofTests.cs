using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using MethodDefinition = AsmResolver.DotNet.MethodDefinition;

namespace Cpp2IL.Core.Tests.Isil;

public class X64FalseBooleanVirtualTailProofTests
{
    [Test]
    [NonParallelizable]
    public void ExactPlayerRequiresLiteralFalsePairedVirtualSlotAndOriginalBooleanSignature()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_FALSE_BOOLEAN_VIRTUAL_TAIL_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_FALSE_BOOLEAN_VIRTUAL_TAIL_INPUT to the neutral player-input directory.");
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
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var owner = app.GetAssemblyByName("FalseBooleanVirtualTailFixture")!
                .Types.Single(type => type.Name == "DispatchNode");
            var caller = owner.Methods.Single(method => method.Name == "ForwardFalse");
            var target = owner.Methods.Single(method => method.Name == "Mark");
            caller.EnsureRawBytes();
            var native = X86Utils.Iterate(caller).ToArray();

            Assert.That(caller.RawBytes.Length, Is.EqualTo(19));
            Assert.That(native, Has.Length.EqualTo(4));
            Assert.That(X64FalseBooleanVirtualTailProof.TryProveShape(native)?
                .MethodPointerOffset, Is.EqualTo(0x178UL));
            Assert.That(X86CallerExceptionRegionProof.Check(caller, native,
                new HashSet<ulong>()), Does.Contain("unproved exit"));
            Assert.That(X86CallerExceptionRegionProof.CheckProvedTerminalIndirectBranch(
                caller, native, native[3].IP), Is.Null);
            var evidence = X64FalseBooleanVirtualTailProof.Find(caller, native);
            Assert.That(evidence, Is.Not.Null);
            Assert.That(evidence!.Target, Is.SameAs(target));
            Assert.That(evidence.Slot, Is.EqualTo(4));
            Assert.That(owner.Definition!.VTable[4]!.AsMethod(),
                Is.SameAs(target.Definition));

            var definition = caller.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            Assert.That(X64FalseBooleanVirtualTailRecovery.TryGenerate(caller,
                definition), Is.True);
            Assert.That(definition.CilMethodBody!.Instructions.Select(instruction =>
                instruction.OpCode).ToArray(), Is.EqualTo(new[]
            {
                CilOpCodes.Ldarg_0, CilOpCodes.Ldc_I4_0,
                CilOpCodes.Callvirt, CilOpCodes.Ret,
            }));

            var wrongReceiver = native.ToArray();
            wrongReceiver[0].MemoryBase = Register.RDX;
            Assert.That(X64FalseBooleanVirtualTailProof.Find(caller,
                wrongReceiver), Is.Null);
            var wrongBooleanRegister = native.ToArray();
            wrongBooleanRegister[1].Op0Register = Register.ECX;
            Assert.That(X64FalseBooleanVirtualTailProof.Find(caller,
                wrongBooleanRegister), Is.Null);
            var nonzeroBoolean = native.ToArray();
            nonzeroBoolean[1].Code = Code.Mov_r32_imm32;
            Assert.That(X64FalseBooleanVirtualTailProof.Find(caller,
                nonzeroBoolean), Is.Null);
            var wrongMethodInfo = native.ToArray();
            wrongMethodInfo[2].MemoryDisplacement64 += 8;
            Assert.That(X64FalseBooleanVirtualTailProof.Find(caller,
                wrongMethodInfo), Is.Null);
            var indexedMethodInfo = native.ToArray();
            indexedMethodInfo[2].MemoryIndex = Register.R9;
            Assert.That(X64FalseBooleanVirtualTailProof.Find(caller,
                indexedMethodInfo), Is.Null);
            var wrongSlot = native.ToArray();
            wrongSlot[3].MemoryDisplacement64 += 16;
            Assert.That(X64FalseBooleanVirtualTailProof.Find(caller,
                wrongSlot), Is.Null);
            var indexedExit = native.ToArray();
            indexedExit[3].MemoryIndex = Register.R9;
            Assert.That(X64FalseBooleanVirtualTailProof.Find(caller,
                indexedExit), Is.Null);
            var directExit = native.ToArray();
            directExit[3].Code = Code.Jmp_rel32_64;
            Assert.That(X64FalseBooleanVirtualTailProof.Find(caller,
                directExit), Is.Null);
            Assert.That(X64FalseBooleanVirtualTailProof.Find(caller,
                native.Append(native[3]).ToArray()), Is.Null);

            var rawParameter = target.Definition!.InternalParameterData![0];
            var originalRawType = rawParameter.RawType!.Type;
            try
            {
                rawParameter.RawType.Type = Il2CppTypeEnum.IL2CPP_TYPE_I4;
                Assert.That(X64FalseBooleanVirtualTailProof.Find(caller, native),
                    Is.Null, "An Int32 target cannot masquerade as a Boolean argument.");
            }
            finally { rawParameter.RawType.Type = originalRawType; }

            try
            {
                target.OverrideAttributes = target.DefaultAttributes &
                    ~MethodAttributes.NewSlot;
                Assert.That(X64FalseBooleanVirtualTailProof.Find(caller, native),
                    Is.Null, "An inherited slot cannot identify the original call declaration.");
            }
            finally { target.OverrideAttributes = null; }

            var originalInterfaceCount = owner.Definition.InterfacesCount;
            var originalOffsetCount = owner.Definition.InterfaceOffsetsCount;
            try
            {
                owner.Definition.InterfacesCount++;
                Assert.That(X64FalseBooleanVirtualTailProof.Find(caller, native), Is.Null);
                owner.Definition.InterfacesCount = originalInterfaceCount;
                owner.Definition.InterfaceOffsetsCount++;
                Assert.That(X64FalseBooleanVirtualTailProof.Find(caller, native), Is.Null);
            }
            finally
            {
                owner.Definition.InterfacesCount = originalInterfaceCount;
                owner.Definition.InterfaceOffsetsCount = originalOffsetCount;
            }

            var aliases = app.MethodsByAddress[caller.UnderlyingPointer];
            aliases.Add(target);
            try
            {
                Assert.That(X64FalseBooleanVirtualTailProof.Find(caller, native),
                    Is.Null, "A shared entry lacks a unique caller identity.");
            }
            finally { aliases.Remove(target); }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
