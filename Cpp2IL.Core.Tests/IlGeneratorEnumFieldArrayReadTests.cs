using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Primitives;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using MethodDefinition = AsmResolver.DotNet.MethodDefinition;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class IlGeneratorEnumFieldArrayReadTests
{
    [Test]
    public void EmissionRevalidatesEnumStorageReadIdentityIndexAndOrder()
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_ENUM_FIELD_ARRAY_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ENUM_FIELD_ARRAY_FIXTURE_INPUT to the neutral exact player input.");
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
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var assembly = app.GetAssemblyByName("EnumFieldArrayFixture")!;
            foreach (var ownerName in new[] { "SignedReader", "UnsignedReader" })
            {
                var owner = assembly.Types.Single(type => type.Name == ownerName);
                var other = assembly.Types.Single(type => type.Name ==
                    (ownerName == "SignedReader" ? "UnsignedTone" : "SignedTone"));
                foreach (var name in new[] { "ReadFirst", "ReadFixed", "ReadAt" })
                {
                    var method = owner.Methods.Single(candidate => candidate.Name == name);
                    method.Analyze();
                    var definition = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
                    Assert.DoesNotThrow(() => IlGenerator.GenerateIl(method, definition));
                    var elementLoad = definition.CilMethodBody!.Instructions.Single(instruction =>
                        instruction.OpCode == CilOpCodes.Ldelem);
                    Assert.That(((ITypeDefOrRef)elementLoad.Operand!).FullName,
                        Is.EqualTo(method.ReturnType.FullName));
                    foreach (var mutation in new[]
                    {
                        "enum-underlying", "enum-layout", "backing-type", "backing-flags", "backing-offset",
                        "array-type", "array-offset", "return-type", "field-site", "element-site", "element-width",
                        "field-receiver", "field-offset", "array-origin", "index", "result-type", "return-origin",
                        "removed-read", "duplicate-read", "reordered-reads", "extra-effect", "disconnected-block"
                    })
                        RejectMutation(method, definition, other, mutation);
                }
            }
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }

    private static void RejectMutation(MethodAnalysisContext method, MethodDefinition definition,
        TypeAnalysisContext otherEnum, string mutation)
    {
        var graph = method.ControlFlowGraph!;
        var instructions = graph.Instructions.ToArray();
        var fieldRead = instructions[0];
        var elementRead = instructions[1];
        var returned = instructions[2];
        var fieldAccess = (FieldReference)fieldRead.Operands[1];
        var access = (ArrayAccess)elementRead.Operands[1];
        var result = (LocalVariable)elementRead.Operands[0];
        var element = method.ReturnType;
        var backing = element.Fields.Single(field => !field.IsStatic);
        var field = fieldAccess.Field;
        var receiver = fieldAccess.Local;
        var fieldOffset = fieldAccess.Offset;
        var originalArray = access.Array;
        var originalIndex = access.Index;
        var originalResultType = result.Type;
        var originalReturn = returned.Operands[0];
        var fieldSite = fieldRead.NativeAddress;
        var elementSite = elementRead.NativeAddress;
        var width = elementRead.IntegerBitWidth;
        var blocks = graph.Blocks.ToArray();
        var blockInstructions = blocks.Select(block => block.Instructions.ToArray()).ToArray();
        var block = graph.FindBlockByInstruction(elementRead)!;
        try
        {
            switch (mutation)
            {
                case "enum-underlying": element.OverrideEnumUnderlyingType = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "enum-layout": element.OverrideAttributes = element.DefaultAttributes | TypeAttributes.ExplicitLayout; break;
                case "backing-type": backing.OverrideFieldType = method.AppContext.SystemTypes.SystemInt64Type; break;
                case "backing-flags": backing.OverrideAttributes = backing.DefaultAttributes & ~FieldAttributes.RTSpecialName; break;
                case "backing-offset": backing.OverrideOffset = 4; break;
                case "array-type": field.OverrideFieldType = new SzArrayTypeAnalysisContext(otherEnum); break;
                case "array-offset": field.OverrideOffset = field.DefaultOffset + 8; break;
                case "return-type": method.OverrideReturnType = otherEnum; break;
                case "field-site": fieldRead.NativeAddress++; break;
                case "element-site": elementRead.NativeAddress++; break;
                case "element-width": elementRead.IntegerBitWidth = 8; break;
                case "field-receiver":
                    fieldAccess.Local = new LocalVariable("otherReceiver", receiver.Register.Copy(), receiver.Type)
                        { IsThis = true };
                    break;
                case "field-offset": fieldAccess.Offset += 8; break;
                case "array-origin":
                    access.Array = new LocalVariable("otherArray", originalArray.Register.Copy(), originalArray.Type);
                    break;
                case "index": access.Index = new Immediate(method.Name == "ReadAt" ? 1 : ((Immediate)originalIndex).Value + 1); break;
                case "result-type": result.Type = method.AppContext.SystemTypes.SystemInt32Type; break;
                case "return-origin": returned.SetOperand(0, new LocalVariable("otherResult", result.Register.Copy(), result.Type)); break;
                case "removed-read": block.Instructions.Remove(elementRead); break;
                case "duplicate-read": block.Instructions.Insert(block.Instructions.IndexOf(elementRead), elementRead); break;
                case "reordered-reads":
                    var fieldPosition = block.Instructions.IndexOf(fieldRead);
                    var readPosition = block.Instructions.IndexOf(elementRead);
                    block.Instructions[fieldPosition] = elementRead;
                    block.Instructions[readPosition] = fieldRead;
                    break;
                case "extra-effect":
                    block.Instructions.Insert(0, new Instruction(999, OpCode.Move, fieldAccess,
                        new Immediate(0)));
                    break;
                case "disconnected-block":
                    var detached = new Block();
                    detached.Instructions.Add(new Instruction(999, OpCode.Return, new Immediate(0)));
                    graph.Blocks.Insert(1, detached);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            Assert.That(() => IlGenerator.GenerateIl(method, definition),
                Throws.TypeOf<DecompilerException>().With.Message.Contains("Enum field-array proof"),
                method.DeclaringType!.Name + "." + method.Name + ": " + mutation);
        }
        finally
        {
            element.OverrideEnumUnderlyingType = null;
            element.OverrideAttributes = null;
            backing.OverrideFieldType = null;
            backing.OverrideAttributes = null;
            backing.OverrideOffset = null;
            field.OverrideFieldType = null;
            field.OverrideOffset = null;
            method.OverrideReturnType = null;
            fieldRead.NativeAddress = fieldSite;
            elementRead.NativeAddress = elementSite;
            elementRead.IntegerBitWidth = width;
            fieldAccess.Local = receiver;
            fieldAccess.Offset = fieldOffset;
            access.Array = originalArray;
            access.Index = originalIndex;
            result.Type = originalResultType;
            returned.SetOperand(0, originalReturn);
            graph.Blocks.Clear();
            graph.Blocks.AddRange(blocks);
            for (var index = 0; index < blocks.Length; index++)
            {
                blocks[index].Instructions.Clear();
                blocks[index].Instructions.AddRange(blockInstructions[index]);
            }
        }
    }
}
