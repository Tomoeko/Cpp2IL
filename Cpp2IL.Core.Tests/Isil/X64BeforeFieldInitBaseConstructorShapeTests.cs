using System;
using System.IO;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.SourceEmission;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace Cpp2IL.Core.Tests.Isil;

public class X64BeforeFieldInitBaseConstructorShapeTests
{
    [Test]
    public void WholeGuardPreservesTheReceiverThroughBothInitializationPaths()
    {
        var body = Body();
        Assert.That(body.Length, Is.EqualTo(17));
        Assert.That(body[^1].NextIP - body[0].IP, Is.EqualTo(73));
        var proof = X64GuardedBaseConstructorProof.TryProveShape(body);
        Assert.That(proof, Is.Not.Null);
        Assert.That(proof!.Value.OnceFlag, Is.EqualTo(body[2].IPRelativeMemoryAddress));
        Assert.That(proof.Value.TypeInfoSlot, Is.EqualTo(body[5].IPRelativeMemoryAddress));
        Assert.That(proof.Value.Tail, Is.EqualTo(body[^1].NearBranchTarget));
    }

    [TestCase("once-edge")]
    [TestCase("class-edge")]
    [TestCase("class-offset")]
    [TestCase("receiver-save")]
    [TestCase("receiver-restore")]
    [TestCase("hidden-argument")]
    [TestCase("tail-call")]
    [TestCase("extra-effect")]
    [TestCase("missing-tail")]
    public void NeitherInitializationNorReceiverEffectsCanBeSkipped(string defect)
    {
        var body = Body();
        switch (defect)
        {
            case "once-edge": body[4].NearBranch64 = body[12].IP; break;
            case "class-edge": body[10].NearBranch64 = body[16].IP; break;
            case "class-offset": body[9].MemoryDisplacement64 += 4; break;
            case "receiver-save": body[3].Op1Register = Register.RDX; break;
            case "receiver-restore": body[13].Op1Register = Register.RAX; break;
            case "hidden-argument": body[12].Op0Register = Register.ECX; break;
            case "tail-call": body[16].Code = Code.Call_rel32_64; break;
            case "extra-effect": body = body.Append(body[11]).ToArray(); break;
            case "missing-tail": body = body[..^1]; break;
        }
        Assert.That(X64GuardedBaseConstructorProof.TryProveShape(body), Is.Null);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void SourceProjectionDistinguishesRelaxedAndExplicitInitialization(bool beforeFieldInit)
    {
        // This authored managed control uses the test runtime. Exact Unity
        // compiler/native equivalence is a separate integration gate.
        var core = typeof(object).Assembly.GetName();
        var reference = new AssemblyReference(core.Name, core.Version!) { PublicKeyOrToken = core.GetPublicKeyToken() };
        var assembly = new AssemblyDefinition("Synthetic.BaseLifetime", new Version(1, 0));
        var module = new ModuleDefinition("Synthetic.BaseLifetime.dll", reference);
        assembly.Modules.Add(module);
        var flags = TypeAttributes.Public | (beforeFieldInit ? TypeAttributes.BeforeFieldInit : 0);
        var type = new TypeDefinition("Synthetic", "BaseLifetime", flags, module.CorLibTypeFactory.Object.Type);
        module.TopLevelTypes.Add(type);
        var field = new FieldDefinition("Marker", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly,
            new FieldSignature(module.CorLibTypeFactory.Int32));
        type.Fields.Add(field);
        var cctor = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static |
            MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName, MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
        type.Methods.Add(cctor);
        cctor.CilMethodBody = new CilMethodBody();
        cctor.CilMethodBody.Instructions.Add(CilOpCodes.Ldc_I4, 17);
        cctor.CilMethodBody.Instructions.Add(CilOpCodes.Stsfld, field);
        cctor.CilMethodBody.Instructions.Add(CilOpCodes.Ret);
        var directory = Path.Combine(Path.GetTempPath(), "cpp2il-base-lifetime-" + Guid.NewGuid().ToString("N"));
        try
        {
            var report = UnitySourceProjectEmitter.Emit([assembly], ["Synthetic.BaseLifetime"],
                [Path.GetDirectoryName(typeof(object).Assembly.Location)!], directory);
            var source = File.ReadAllText(Path.Combine(directory, report.Assemblies.Single().SourceFile));
            if (beforeFieldInit)
            {
                Assert.That(source, Does.Contain("readonly int Marker = 17;"));
                Assert.That(source, Does.Not.Contain("static BaseLifetime()"));
            }
            else Assert.That(source, Does.Contain("static BaseLifetime()"));
            Assert.That(type.IsBeforeFieldInit, Is.EqualTo(beforeFieldInit));
            Assert.That(report.UnityCompilation, Is.EqualTo("unverified"));
            Assert.That(report.BehavioralValidation, Is.EqualTo("unverified"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static Iced.Intel.Instruction[] Body()
    {
        var assembler = new Assembler(64);
        var flag = assembler.CreateLabel(); var slot = assembler.CreateLabel();
        var metadata = assembler.CreateLabel(); var initialize = assembler.CreateLabel();
        var ready = assembler.CreateLabel(); var called = assembler.CreateLabel(); var tail = assembler.CreateLabel();
        // Keep the two-byte prologue PUSH consumed by the complete unwind frame.
        assembler.db(new byte[] { 0x40, 0x53 }); assembler.sub(rsp, 0x20);
        assembler.cmp(__byte_ptr[flag], 0);
        // Select the register-load MOV encoding consumed by the existing recipe.
        assembler.db(new byte[] { 0x48, 0x8b, 0xd9 });
        assembler.jne(ready); assembler.lea(rcx, __qword_ptr[slot]); assembler.call(metadata);
        assembler.mov(__byte_ptr[flag], 1); assembler.Label(ref ready);
        assembler.mov(rcx, __qword_ptr[slot]);
        assembler.cmp(__dword_ptr[rcx + checked((int)Il2CppClassLayout.CctorFinishedOrNoCctorOffset64)], 0);
        assembler.jne(called); assembler.call(initialize); assembler.Label(ref called);
        assembler.db(new byte[] { 0x33, 0xd2 }); assembler.db(new byte[] { 0x48, 0x8b, 0xcb });
        assembler.add(rsp, 0x20); assembler.pop(rbx); assembler.jmp(tail);
        assembler.db(new byte[200]); assembler.Label(ref tail); assembler.ret();
        assembler.Label(ref metadata); assembler.ret(); assembler.Label(ref initialize); assembler.ret();
        assembler.Label(ref flag); assembler.db(0); assembler.Label(ref slot); assembler.db(new byte[8]);
        using var stream = new MemoryStream(); assembler.Assemble(new StreamCodeWriter(stream), 0x4000);
        return X86Utils.Disassemble(stream.ToArray(), 0x4000, false).Take(17).ToArray();
    }
}
