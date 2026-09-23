using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Cpp2IL.Core.Utils.AsmResolver;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private const string HelpersNamespace = "Cpp2ILInjected";
    private const string HelpersTypeName = "Cpp2ILHelpers";
    private const string NoteIssueMethodName = "NoteDecompilerIssue";

    public static void InjectHelpersType(ApplicationAnalysisContext appContext)
    {
        var helpersType = appContext.InjectTypeIntoAllAssemblies(
            HelpersNamespace,
            HelpersTypeName,
            null,
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);

        helpersType.InjectMethodToAllAssemblies(
            NoteIssueMethodName,
            appContext.SystemTypes.SystemVoidType,
            MethodAttributes.Public | MethodAttributes.Static,
            [appContext.SystemTypes.SystemStringType]);
    }

    private sealed class EmissionLocals
    {
        private readonly Dictionary<LocalVariable, CilLocalVariable> _locals = [];
        public MethodAnalysisContext Context { get; }
        public Dictionary<LocalVariable, Parameter> Parameters { get; } = [];
        public Dictionary<LocalVariable, ParameterAnalysisContext> ParameterContexts { get; } = [];
        public CilLocalVariable this[LocalVariable local] => _locals[local];
        public void Add(LocalVariable local, CilLocalVariable definition) => _locals.Add(local, definition);

        public EmissionLocals(MethodAnalysisContext context, MethodDefinition definition)
        {
            Context = context;
            foreach (var local in context.ParameterLocals)
            {
                if (local.IsMethodInfo)
                    continue;
                if (local.IsThis)
                {
                    if (context.IsStatic)
                        throw new DecompilerException("Static method has an instance parameter");
                    Parameters.Add(local, definition.Parameters.ThisParameter!);
                    continue;
                }

                var offset = context.IsStatic ? 0 : 1;
                var matches = Enumerable.Range(0, context.Parameters.Count).Where(index =>
                    index + offset < context.ParameterOperands.Count &&
                    context.ParameterOperands[index + offset] is Register register &&
                    local.Register.Number == register.Number && local.Register.Version == -1).ToArray();
                if (matches.Length != 1)
                    throw new DecompilerException("Managed parameter cannot be mapped to a unique native argument");
                Parameters.Add(local, definition.Parameters[matches[0]]);
                ParameterContexts.Add(local, context.Parameters[matches[0]]);
            }
        }
    }

    public static void GenerateIl(MethodAnalysisContext context, MethodDefinition definition)
    {
        ValidateCallSemantics(context);

        // Diagnose retained lifting failures before local typing. An unsupported operation
        // often also leaves its result untyped; that secondary error must not hide the cause.
        var unsupported = context.ControlFlowGraph!.Instructions.Where(i =>
            i.OpCode is OpCode.Invalid or OpCode.NotImplemented or OpCode.Interrupt or OpCode.UnresolvedValue or OpCode.Phi).ToArray();
        if (unsupported.Length != 0)
            throw new DecompilerException($"Unsupported instructions ({unsupported.Length}): " +
                string.Join("; ", unsupported.Take(12).Select(i => i.ToString())) +
                (unsupported.Length > 12 ? "; additional failures omitted" : ""));

        NarrowFieldEqualityProof.Validate(context);

        // Native return registers can remain live even when metadata identifies a void callee.
        // There is no managed value to store in that case. Check before constructor fusion can
        // erase the call, otherwise InitializeLocals would silently supply a fabricated zero.
        foreach (var call in context.ControlFlowGraph!.Instructions.Where(i =>
                     i.OpCode == OpCode.Call && i.Operands[0] is MethodAnalysisContext { IsVoid: true }))
        {
            if (call.Destination is not { } destination)
                continue;
            if (destination is not LocalVariable result || context.ControlFlowGraph.Instructions.Any(i =>
                    OperandEffects.ReadLocals(i).Contains(result)))
                throw new DecompilerException("A void managed call has a consumed native return value");
        }

        // Change branch targets to instructions
        foreach (var instruction in context.ControlFlowGraph!.Blocks.SelectMany(block => block.Instructions))
        {
            if (instruction.Operands.Count > 0 && instruction.Operands[0] is Block target)
            {
                if (target.Instructions.Count > 0)
                    instruction.SetOperand(0, target.Instructions[0]);
            }
        }

        var body = new CilMethodBody()
        {
            InitializeLocals = true, // Without this ILSpy does: CompilerServices.Unsafe.SkipInit(out object obj);
            ComputeMaxStackOnBuild = true,
            VerifyLabelsOnBuild = true
        };

        definition.CilMethodBody = body;

        // Make sure context.Locals actually has all locals (idk why it doesn't sometimes)
        foreach (var operand in context.ControlFlowGraph.Instructions.SelectMany(i => i.Operands))
        {
            LocalVariable? local = null;

            if (operand is FieldReference field)
                local = field.Local;

            if (operand is LocalVariable local2)
                local = local2;

            if (operand is MemoryOperand memory && memory.Base is LocalVariable local3)
                local = local3;

            var elementOperand = operand is AddressOf { Target: ArrayAccess elementAddress } ? elementAddress : operand;

            if (elementOperand is ArrayAccess arrayAccess)
            {
                local = arrayAccess.Array;

                if (arrayAccess.Index is LocalVariable index && !context.Locals.Contains(index))
                    context.Locals.Add(index);
            }

            if (operand is ArrayLength arrayLength)
                local = arrayLength.Array;

            if (operand is AddressOf { Target: LocalVariable addressed })
                local = addressed;

            if (local != null && !context.Locals.Contains(local))
                context.Locals.Add(local);
        }

        // Map ISIL locals to IL
        var locals = new EmissionLocals(context, definition);
        foreach (var local in context.Locals)
        {
            TypeSignature ilType;

            // An invented object local can hide an invalid stack type from the output consumer.
            if (local.Type != null && local.Type != context.AppContext.SystemTypes.SystemVoidType)
                ilType = local.Type.ToTypeSignature();
            else
                throw new DecompilerException($"Local type is unresolved: {local.Name}");

            var ilLocal = new CilLocalVariable(ilType);
            body.LocalVariables.Add(ilLocal);
            locals.Add(local, ilLocal);
        }

        // Generate IL
        Dictionary<Instruction, List<CilInstruction>> instructionMap = [];
        Dictionary<Block, CilInstruction> blockEntryMap = [];
        List<(CilInstruction BranchInstruction, Block TargetBlock)> pendingBlockBranchFixups = [];

        foreach (var block in context.ControlFlowGraph!.Blocks)
        {
            if (block == context.ControlFlowGraph.EntryBlock || block == context.ControlFlowGraph.ExitBlock)
                continue;

            if (block.Instructions.Count == 0)
                continue;

            foreach (var instruction in block.Instructions)
            {
                var generated = GenerateInstructions(instruction, context, definition, locals);
                instructionMap.Add(instruction, generated);

                if (!blockEntryMap.ContainsKey(block) && generated.Count > 0)
                    blockEntryMap[block] = generated[0];
            }

            var lastInstruction = block.Instructions.Last();
            
            if (lastInstruction.OpCode == OpCode.ConditionalJump)
            {
                var trueTarget = TryResolveJumpTargetBlock(lastInstruction, context.ControlFlowGraph);
                var falseSuccessor = block.Successors.FirstOrDefault(s => s != trueTarget && s != context.ControlFlowGraph.ExitBlock);
                if (falseSuccessor == null) continue;
                var bridge = new CilInstruction(CilOpCodes.Br, new CilInstructionLabel());
                definition.CilMethodBody!.Instructions.Add(bridge);
                pendingBlockBranchFixups.Add((bridge, falseSuccessor));
            }

            else if (lastInstruction.OpCode != OpCode.Jump && lastInstruction.OpCode != OpCode.Return && lastInstruction.OpCode != OpCode.IndirectJump)
            {
                var successor = block.Successors.FirstOrDefault(s => s != context.ControlFlowGraph.ExitBlock);
                if (successor == null) continue;
                var bridge = new CilInstruction(CilOpCodes.Br, new CilInstructionLabel());
                definition.CilMethodBody!.Instructions.Add(bridge);
                pendingBlockBranchFixups.Add((bridge, successor));
            }
        }
        // Set IL branch targets
        foreach (var kvp in instructionMap)
        {
            var instruction = kvp.Key;
            var il = kvp.Value;

            if (instruction.OpCode == OpCode.Jump || instruction.OpCode == OpCode.ConditionalJump)
            {
                var ilBranch = il.First(i => i.OpCode == CilOpCodes.Br || i.OpCode == CilOpCodes.Brtrue);

                if (instruction.Operands[0] is Block targetBlock)
                {
                    throw new DecompilerException($"Branch target block not in CFG: {targetBlock}");
                }

                var target = (Instruction)instruction.Operands[0];

                if (!instructionMap.ContainsKey(target))
                {
                    throw new DecompilerException($"Branch target not in ISIL to IL map: {target}");
                }

                ilBranch.Operand = new CilInstructionLabel(instructionMap[target][0]);
            }
        }
        
        foreach (var (branchInstruction, targetBlock) in pendingBlockBranchFixups)
        {
            var target = ResolveBlockEntryInstruction(targetBlock, blockEntryMap);
            if (target == null)
            {
                throw new DecompilerException($"Unable to resolve branch target block: {targetBlock}");
            }

            branchInstruction.Operand = new CilInstructionLabel(target);
        }

        // Validate here, while the output format can still attribute failure to this method.
        // This verifies branch labels and stack depth, not full type safety or equivalence.
        try
        {
            body.VerifyLabels();
            body.ComputeMaxStack();
        }
        catch (System.Exception exception) when (exception is InvalidCilInstructionException or StackImbalanceException or System.AggregateException)
        {
            throw new DecompilerException("Generated IL has invalid control flow or stack depth", exception);
        }
    }

    private static Block? TryResolveJumpTargetBlock(Instruction jumpInstruction, ISILControlFlowGraph cfg)
    {
        if (jumpInstruction.Operands.Count == 0)
            return null;

        if (jumpInstruction.Operands[0] is Block targetBlock)
            return targetBlock;

        if (jumpInstruction.Operands[0] is Instruction targetInstruction)
            return cfg.FindBlockByInstruction(targetInstruction);

        return null;
    }

    private static CilInstruction? ResolveBlockEntryInstruction(Block block,
        Dictionary<Block, CilInstruction> blockEntryMap, HashSet<Block>? visited = null)
    {
        if (blockEntryMap.TryGetValue(block, out var target))
            return target;

        visited ??= [];
        if (!visited.Add(block))
            return null;

        foreach (var successor in block.Successors)
        {
            var resolved = ResolveBlockEntryInstruction(successor, blockEntryMap, visited);
            if (resolved != null)
                return resolved;
        }
        return null;
    }

    private static List<CilInstruction> GenerateInstructions(Instruction instruction, MethodAnalysisContext context,
        MethodDefinition method, EmissionLocals locals)
    {
        var body = method.CilMethodBody!;
        var instructions = body.Instructions;
        var startIndex = instructions.Count;

        switch (instruction.OpCode)
        {
            case OpCode.Invalid:
            case OpCode.NotImplemented:
            case OpCode.Interrupt:
            case OpCode.UnresolvedValue:
                throw new DecompilerException($"Unsupported instruction: {instruction}");

            case OpCode.Nop:
                instructions.Add(CilOpCodes.Nop);
                break;

            case OpCode.Move:
                if (instruction.Operands[0] is FieldReference field) // stfld takes instance before value so LoadOperand StoreToOperand doesn't work
                {
                    if (!field.Field.IsStatic)
                        LoadLocal(field.Local, method, locals);

                    LoadOperand(instruction.Operands[1], method, locals, field.Field.FieldType);
                    instructions.Add(field.Field.IsStatic ? CilOpCodes.Stsfld : CilOpCodes.Stfld, field.Field.ToFieldDescriptor());
                    break;
                }

                // stelem needs array and index before the value, so like stfld it can't go through LoadOperand/StoreToOperand.
                // This also lets ILSpy handle it as a proper array initializer
                if (instruction.Operands[0] is ArrayAccess { Array.Type: SzArrayTypeAnalysisContext { ElementType: { } stored } } target)
                {
                    LoadLocal(target.Array, method, locals);
                    LoadOperand(target.Index, method, locals);
                    LoadOperand(instruction.Operands[1], method, locals, stored);
                    instructions.Add(CilOpCodes.Stelem, stored.ToTypeSignature().ToTypeDefOrRef());
                    break;
                }

                LoadOperand(instruction.Operands[1], method, locals, DestinationType(instruction.Operands[0]));
                StoreToOperand(instruction.Operands[0], method, locals);
                break;

            case OpCode.NewArr:
                if (instruction.Operands is [_, SzArrayTypeAnalysisContext { ElementType: { } newArrayElement }, { } length])
                {
                    LoadOperand(length, method, locals);
                    instructions.Add(CilOpCodes.Newarr, newArrayElement.ToTypeSignature().ToTypeDefOrRef());
                }
                else
                    throw new DecompilerException($"Array allocation is unresolved: {instruction}");

                StoreToOperand(instruction.Operands[0], method, locals);
                break;

            case OpCode.Newobj:
                // Try and fuse our Newobj + the follow up constructor CallVoid into one IL newobj.
                // An allocation without a recovered constructor cannot be replaced by null.
                if (FindConstructorCall(context, instruction) is { Operands: [MethodAnalysisContext constructor, _, ..] } constructorCall)
                {
                    // Operands run [ctor, newObject, arguments..., methodInfo], so take only as many as
                    // the constructor declares (i.e. drop methodInfo)
                    var constructorArgs = constructorCall.Operands.Skip(ConstructorReceiverIndex(constructorCall) + 1).Take(constructor.Parameters.Count).ToList();
                    if (constructorArgs.Count != constructor.Parameters.Count)
                        throw new DecompilerException("Constructor arguments are incomplete");
                    for (var i = 0; i < constructorArgs.Count; i++)
                        LoadOperand(constructorArgs[i], method, locals, constructor.Parameters[i].ParameterType);

                    instructions.Add(CilOpCodes.Newobj, constructor.ToMethodDescriptor());
                    StoreToOperand(instruction.Operands[0], method, locals);

                    constructorCall.OpCode = OpCode.Nop;
                    constructorCall.SetOperands();
                }
                else
                    throw new DecompilerException("Object allocation has no recovered constructor call");
                break;

            case OpCode.Box:
                if (instruction.Operands is [_, TypeAnalysisContext boxedType, var boxedValue])
                {
                    // il2cpp_value_box takes the value by address, but IL boxes it by value
                    LoadOperand(boxedValue is AddressOf { Target: LocalVariable byRef } ? byRef : boxedValue, method, locals, boxedType);
                    instructions.Add(CilOpCodes.Box, boxedType.ToTypeSignature().ToTypeDefOrRef());
                }
                else
                    throw new DecompilerException($"Boxing operands are unresolved: {instruction}");

                StoreToOperand(instruction.Operands[0], method, locals);
                break;

            case OpCode.Throw:
                if (instruction.Operands is [TypeAnalysisContext exceptionType]
                    && exceptionType.Methods.FirstOrDefault(m => m.Name == ".ctor" && m.Parameters.Count == 0) is { } exceptionCtor)
                    instructions.Add(CilOpCodes.Newobj, exceptionCtor.ToMethodDescriptor());
                else if (instruction.Operands is [LocalVariable or FieldReference])
                    LoadOperand(instruction.Operands[0], method, locals); // an already-constructed exception
                else
                    throw new DecompilerException($"Exception operand is unresolved: {instruction}");

                instructions.Add(CilOpCodes.Throw);
                break;

            case OpCode.Phi:
                throw new DecompilerException("Phi instruction survived SSA lowering");

            case OpCode.Call:
            case OpCode.CallVoid:
                if (instruction.Operands[0] is not MethodAnalysisContext targetMethod)
                    throw new DecompilerException($"Call target is unresolved: {instruction.Operands[0]}");

                if (instruction.CallSemantics == CallSemantics.NullCheckedInstance)
                    ValidateNullCheckedParameterTypes(instruction, locals);

                var importedMethod = targetMethod.ToMethodDescriptor();

                var thisParamIndex = instruction.OpCode == OpCode.Call ? 2 : 1;

                if (!targetMethod.IsStatic) // Load 'this' param
                {
                    if ((instruction.Operands.Count - 1) >= thisParamIndex)
                        LoadInstanceCallReceiver(instruction.Operands[thisParamIndex], targetMethod, method, locals);
                    else
                        throw new DecompilerException("Instance call has no recovered receiver");
                }

                // Load normal params
                var callParamIndex = instruction.OpCode == OpCode.Call ? (targetMethod.IsStatic ? 2 : 3) : (targetMethod.IsStatic ? 1 : 2);
                // A call whose target was only identified after lifting still carries the operands the
                // unknown-callee convention gave it, which may be fewer than the method actually takes.
                // Missing arguments must fail recovery; inventing values changes the call's behavior.
                var availableArgs = instruction.Operands.Count - callParamIndex;
                for (var i = 0; i < targetMethod.Parameters.Count; i++)
                {
                    var parameterType = targetMethod.Parameters[i].ParameterType;

                    if (i < availableArgs)
                        LoadOperand(instruction.Operands[callParamIndex + i], method, locals, parameterType);
                    else
                        throw new DecompilerException($"Call argument {i} is unresolved");
                }

                instructions.Add(instruction.CallSemantics == CallSemantics.NullCheckedInstance
                    ? CilOpCodes.Callvirt : CilOpCodes.Call, importedMethod);

                // the lifter's guess at whether the callee returns anything can disagree with the
                // signature we later resolved, so go by the signature and balance the stack
                if (!targetMethod.IsVoid)
                {
                    if (instruction.OpCode == OpCode.Call)
                        StoreToOperand(instruction.Operands[1], method, locals);
                    else
                        instructions.Add(CilOpCodes.Pop);
                }

                break;

            case OpCode.IndirectCall:
                throw new DecompilerException("Indirect call survived call resolution");

            case OpCode.Return:
                if (!context.IsVoid)
                {
                    if (instruction.Operands.Count == 1)
                        LoadOperand(instruction.Operands[0], method, locals, context.ReturnType);
                    else
                        throw new DecompilerException("Return value is unresolved");
                }
                instructions.Add(CilOpCodes.Ret);
                break;

            case OpCode.Jump:
                instructions.Add(CilOpCodes.Br, new CilInstructionLabel());
                break;

            case OpCode.ConditionalJump:
                LoadOperand(instruction.Operands[1], method, locals);
                instructions.Add(CilOpCodes.Brtrue, new CilInstructionLabel());
                break;

            case OpCode.IndirectJump:
            case OpCode.ShiftStack:
                throw new DecompilerException($"Unresolved control-flow instruction: {instruction.OpCode}");

            case OpCode.ShiftLeft:
            case OpCode.ShiftRight:
            case OpCode.ShiftRightUnsigned:
                var shiftWidth = instruction.IntegerBitWidth;
                if (shiftWidth is not (32 or 64))
                    throw new DecompilerException("Shift requires an established 32/64-bit native operand width");
                if (IntegerStackWidth(DestinationType(instruction.Operands[0])) != shiftWidth)
                    throw new DecompilerException("Shift destination does not preserve the established native operand width");
                LoadArithmeticOperand(instruction.Operands[1], shiftWidth, method, locals);
                // x64 consumes CL or an immediate, then masks to 5/6 bits. CIL requires an
                // Int32/native-int count even for an Int64 value; do not widen both operands.
                if (instruction.Operands[2] is Immediate shiftCount)
                    instructions.Add(CilOpCodes.Ldc_I4, unchecked((int)shiftCount.Value));
                else
                {
                    var countWidth = IntegerStackWidth(DestinationType(instruction.Operands[2]));
                    if (countWidth is not (32 or 64))
                        throw new DecompilerException("Shift count has no established integer representation");
                    LoadOperand(instruction.Operands[2], method, locals);
                    if (countWidth == 64)
                        instructions.Add(CilOpCodes.Conv_I4);
                }
                instructions.Add(CilOpCodes.Ldc_I4, shiftWidth - 1);
                instructions.Add(CilOpCodes.And);
                instructions.Add(instruction.OpCode switch
                {
                    OpCode.ShiftRightUnsigned => CilOpCodes.Shr_Un,
                    OpCode.ShiftRight => CilOpCodes.Shr,
                    _ => CilOpCodes.Shl,
                });
                StoreToOperand(instruction.Operands[0], method, locals);
                break;

            case OpCode.FloatCompare:
                EmitFloatingComparison(instruction, method, locals);
                break;

            case OpCode.FloatTruncateSigned:
                EmitFloatingTruncation(instruction, method, locals);
                break;

            case OpCode.IntegerExtend:
                EmitIntegerExtension(instruction, method, locals);
                break;

            case OpCode.CheckEqual:
            case OpCode.CheckGreater:
            case OpCode.CheckLess:
            case OpCode.CheckNotEqual:
            case OpCode.CheckGreaterOrEqual:
            case OpCode.CheckLessOrEqual:
            case OpCode.CheckLessUnsigned:
            case OpCode.CheckGreaterUnsigned:
            case OpCode.CheckLessOrEqualUnsigned:
            case OpCode.CheckGreaterOrEqualUnsigned:

            case OpCode.Add:
            case OpCode.Subtract:
            case OpCode.Multiply:
            case OpCode.Divide:
            case OpCode.Modulo:
            case OpCode.DivideUnsigned:
            case OpCode.ModuloUnsigned:

            case OpCode.And:
            case OpCode.Or:
            case OpCode.Xor:
                if (instruction.OpCode is OpCode.DivideUnsigned or OpCode.ModuloUnsigned && instruction.IntegerBitWidth is not (32 or 64))
                    throw new DecompilerException("Unsigned division requires an established 32/64-bit native width");
                if (instruction.OpCode is OpCode.Divide or OpCode.Modulo or OpCode.DivideUnsigned or OpCode.ModuloUnsigned &&
                    instruction.IntegerBitWidth != 0 && IntegerStackWidth(DestinationType(instruction.Operands[0])) != instruction.IntegerBitWidth)
                    throw new DecompilerException("Native division destination width does not match its recovered managed type");

                // klass pointer read => GetType
                if (instruction.OpCode is OpCode.CheckEqual or OpCode.CheckNotEqual
                    && TryEmitExactTypeComparison(instruction, method, locals))
                    break;

                // Float arithmetic on a promoted integer operand needs an explicit conversion, so both
                // operands are coerced to the (float) result type. A no-op when they already match.
                var floatConversion = FloatArithmeticConversion(instruction);

                if (instruction.IntegerBitWidth is 8 or 16 && instruction.OpCode is OpCode.CheckEqual or OpCode.CheckNotEqual)
                {
                    // The preflight proved one captured byte/word field compared only with zero.
                    // Both signed and unsigned managed extensions preserve that predicate.
                    LoadOperand(instruction.Operands[1], method, locals);
                    LoadOperand(instruction.Operands[2], method, locals);
                }
                else
                {
                    LoadArithmeticOperand(instruction.Operands[1], instruction.IntegerBitWidth, method, locals);
                    if (floatConversion is { } conv1)
                        instructions.Add(conv1);
                    LoadArithmeticOperand(instruction.Operands[2], instruction.IntegerBitWidth, method, locals);
                    if (floatConversion is { } conv2)
                        instructions.Add(conv2);
                }

                switch (instruction.OpCode)
                {
                    case OpCode.CheckEqual: instructions.Add(CilOpCodes.Ceq); break;
                    case OpCode.CheckGreater: instructions.Add(CilOpCodes.Cgt); break;
                    case OpCode.CheckLess: instructions.Add(CilOpCodes.Clt); break;
                    case OpCode.CheckGreaterUnsigned: instructions.Add(CilOpCodes.Cgt_Un); break;
                    case OpCode.CheckLessUnsigned: instructions.Add(CilOpCodes.Clt_Un); break;

                    // a != b  ==  (a == b) == 0
                    case OpCode.CheckNotEqual:
                        instructions.Add(CilOpCodes.Ceq);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    // a >= b  ==  !(a < b)
                    case OpCode.CheckGreaterOrEqual:
                        instructions.Add(CilOpCodes.Clt);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    // a <= b  ==  !(a > b)
                    case OpCode.CheckLessOrEqual:
                        instructions.Add(CilOpCodes.Cgt);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    case OpCode.CheckGreaterOrEqualUnsigned:
                        instructions.Add(CilOpCodes.Clt_Un);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    case OpCode.CheckLessOrEqualUnsigned:
                        instructions.Add(CilOpCodes.Cgt_Un);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;

                    case OpCode.Add: instructions.Add(CilOpCodes.Add); break;
                    case OpCode.Subtract: instructions.Add(CilOpCodes.Sub); break;
                    case OpCode.Multiply: instructions.Add(CilOpCodes.Mul); break;
                    case OpCode.Divide: instructions.Add(CilOpCodes.Div); break;
                    case OpCode.Modulo: instructions.Add(CilOpCodes.Rem); break;
                    case OpCode.DivideUnsigned: instructions.Add(CilOpCodes.Div_Un); break;
                    case OpCode.ModuloUnsigned: instructions.Add(CilOpCodes.Rem_Un); break;

                    case OpCode.And: instructions.Add(CilOpCodes.And); break;
                    case OpCode.Or: instructions.Add(CilOpCodes.Or); break;
                    case OpCode.Xor: instructions.Add(CilOpCodes.Xor); break;
                }

                StoreToOperand(instruction.Operands[0], method, locals);
                break;

            case OpCode.Not:
            case OpCode.Negate:
                LoadOperand(instruction.Operands[1], method, locals);

                if (instruction.OpCode == OpCode.Negate)
                    instructions.Add(CilOpCodes.Neg);
                else if (IsBoolean(instruction.Operands[1], context))
                {
                    instructions.Add(CilOpCodes.Ldc_I4_0);
                    instructions.Add(CilOpCodes.Ceq);
                }
                else
                    instructions.Add(CilOpCodes.Not);

                StoreToOperand(instruction.Operands[0], method, locals);
                break;

            default:
                throw new DecompilerException($"Unsupported instruction: {instruction.OpCode}");
        }

        return instructions.ToList().GetRange(startIndex, instructions.Count - startIndex); // Return added IL
    }
    
    private static int ConstructorReceiverIndex(Instruction constructorCall) => constructorCall.OpCode == OpCode.CallVoid ? 1 : 2;

    // Try find the follow up CallVoid for a constructor, after a Newobj.
    private static Instruction? FindConstructorCall(MethodAnalysisContext context, Instruction newobj)
    {
        var newObject = newobj.Operands[0];

        // Fusion moves argument loading to the allocation. Only adjacent operations within
        // one block are currently proved safe; crossing another operation or a branch is not.
        var block = context.ControlFlowGraph!.FindBlockByInstruction(newobj);
        if (block == null)
            return null;
        var instructions = block.Instructions;
        var index = instructions.IndexOf(newobj);
        for (var i = index + 1; i < instructions.Count; i++)
        {
            var candidate = instructions[i];
            if (candidate.OpCode == OpCode.Nop)
                continue;
            if (candidate is not { OpCode: OpCode.Call or OpCode.CallVoid, Operands: [MethodAnalysisContext { Name: ".ctor" }, ..] })
                return null;
            var receiver = ConstructorReceiverIndex(candidate);
            return candidate.Operands.Count > receiver && ReferenceEquals(candidate.Operands[receiver], newObject)
                ? candidate
                : null;
        }

        return null;
    }

    private static CilOpCode? FloatArithmeticConversion(Instruction instruction)
    {
        if (instruction.OpCode is not (OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Modulo))
            return null;

        return (instruction.Operands[0] as LocalVariable)?.Type?.FullName switch
        {
            "System.Single" => CilOpCodes.Conv_R4,
            "System.Double" => CilOpCodes.Conv_R8,
            _ => null,
        };
    }

    private static int IntegerStackWidth(TypeAnalysisContext? type)
    {
        if (type?.IsEnumType == true)
            type = type.EnumUnderlyingType;
        return type?.FullName switch
        {
            "System.Boolean" or "System.Byte" or "System.SByte" or "System.Char" or
                "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" => 32,
            "System.Int64" or "System.UInt64" => 64,
            _ => 0
        };
    }

    private static void LoadArithmeticOperand(IOperand operand, int nativeWidth, MethodDefinition method, EmissionLocals locals)
    {
        if (nativeWidth == 0)
        {
            if (DestinationType(operand) is { IsValueType: true, IsEnumType: false } type &&
                type.Type is Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE or Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST)
                throw new DecompilerException("Managed struct arithmetic requires a proved native scalar projection");
            LoadOperand(operand, method, locals);
            return;
        }
        if (nativeWidth is not (32 or 64))
            throw new DecompilerException("Native integer width is not supported by managed arithmetic emission");
        if (operand is Immediate immediate)
        {
            var instructions = method.CilMethodBody!.Instructions;
            if (nativeWidth == 32)
                instructions.Add(CilOpCodes.Ldc_I4, unchecked((int)immediate.Value));
            else
                instructions.Add(CilOpCodes.Ldc_I8, immediate.Value);
            return;
        }
        if (IntegerStackWidth(DestinationType(operand)) != nativeWidth)
        {
            if (TryGetScalarStructParameterField(operand, nativeWidth, locals) is not { } field)
                throw new DecompilerException("Native integer operand width does not match its recovered managed type");
            LoadOperand(field, method, locals);
            return;
        }
        LoadOperand(operand, method, locals);
    }

    private static FieldReference? TryGetScalarStructParameterField(IOperand operand, int nativeWidth, EmissionLocals locals)
    {
        // Windows x64 passes 32/64-bit aggregates in integer argument slots. This is a
        // projection of an evidenced by-value parameter, not a change to its managed type.
        // The size proof is the boxed instance size minus the object header, combined with
        // blittability, default sequential layout and a single full-width primitive field.
        if (operand is not LocalVariable { IsThis: false, IsMethodInfo: false, Type: { } type } local ||
            !locals.ParameterContexts.TryGetValue(local, out var parameter) || parameter.IsRef ||
            !ReferenceEquals(parameter.ParameterType, type) || !ReferenceEquals(parameter.DefaultParameterType, type) ||
            locals.Context.AppContext.Binary is not PE { PointerSizeBytes: 8 } binary ||
            binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
            type is GenericInstanceTypeAnalysisContext || type.GenericParameters.Count != 0 ||
            type.Definition is not { IsValueType: true, IsEnumType: false, IsBlittable: true,
                IsImportOrWindowsRuntime: false, IsByRefLike: false,
                PackingSizeIsDefault: true, ClassSizeIsDefault: true } definition ||
            (definition.Attributes & TypeAttributes.LayoutMask) != TypeAttributes.SequentialLayout ||
            TypeSizes.UnboxedSize(type, binary.PointerSizeBytes) != nativeWidth / 8)
            return null;

        var fields = type.Fields.Where(f => !f.IsStatic).ToArray();
        if (fields.Length != 1 || fields[0] is not { Offset: 0, DefaultOffset: 0 } field ||
            field.Attributes.HasFlag(FieldAttributes.HasFieldMarshal) ||
            field.DefaultAttributes.HasFlag(FieldAttributes.HasFieldMarshal) ||
            !ReferenceEquals(field.FieldType, field.DefaultFieldType) ||
            (field.Visibility != FieldAttributes.Public && !ReferenceEquals(field.DeclaringType, locals.Context.DeclaringType)))
            return null;

        var expectedWidth = field.FieldType.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 => 32,
            Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 => 64,
            _ => 0
        };
        return expectedWidth == nativeWidth ? new FieldReference(field, local, 0) : null;
    }

    private static void LoadOperand(IOperand operand, MethodDefinition method,
        EmissionLocals locals,
        TypeAnalysisContext? expectedType = null)
    {
        var instructions = method.CilMethodBody!.Instructions;

        var module = method.DeclaringModule!;

        // A null reference reaches us as an integer zero, which would otherwise be emitted as a literal 0
        // and read back as a cast from a number.
        if (expectedType is { IsValueType: false } and not ByRefTypeAnalysisContext and not PointerTypeAnalysisContext
            && IsZeroConstant(operand))
        {
            instructions.Add(CilOpCodes.Ldnull);
            return;
        }

        switch (operand)
        {
            case Immediate immediate when IntegerStackWidth(expectedType) == 64:
                instructions.Add(CilOpCodes.Ldc_I8, immediate.Value);
                break;
            case Immediate immediate when IntegerStackWidth(expectedType) == 32:
                instructions.Add(CilOpCodes.Ldc_I4, unchecked((int)immediate.Value));
                break;
            case Immediate { Value: >= int.MinValue and <= int.MaxValue } immediate:
                instructions.Add(CilOpCodes.Ldc_I4, (int)immediate.Value);
                break;
            case Immediate immediate:
                instructions.Add(CilOpCodes.Ldc_I8, immediate.Value);
                break;
            case FloatLiteral f:
                instructions.Add(CilOpCodes.Ldc_R4, f.Value);
                break;
            case DoubleLiteral d:
                instructions.Add(CilOpCodes.Ldc_R8, d.Value);
                break;
            case StringLiteral s:
                instructions.Add(CilOpCodes.Ldstr, s.Value);
                break;
            case LocalVariable local:
                LoadLocal(local, method, locals);
                break;
            case ArrayLength arrayLength:
                LoadLocal(arrayLength.Array, method, locals);
                instructions.Add(CilOpCodes.Ldlen);
                instructions.Add(CilOpCodes.Conv_I4);
                break;
            case AddressOf { Target: LocalVariable addressed }:
                if (locals.Parameters.TryGetValue(addressed, out var addressedParameter))
                    instructions.Add(CilOpCodes.Ldarga, addressedParameter);
                else
                    instructions.Add(CilOpCodes.Ldloca, locals[addressed]);
                break;
            case AddressOf { Target: ArrayAccess elementAddress }:
                LoadLocal(elementAddress.Array, method, locals);
                LoadOperand(elementAddress.Index, method, locals);
                instructions.Add(CilOpCodes.Ldelema,
                    ((SzArrayTypeAnalysisContext)elementAddress.Array.Type!).ElementType.ToTypeSignature().ToTypeDefOrRef());
                break;
            case ArrayAccess arrayAccess:
                LoadLocal(arrayAccess.Array, method, locals);
                LoadOperand(arrayAccess.Index, method, locals);
                instructions.Add(CilOpCodes.Ldelem,
                    ((SzArrayTypeAnalysisContext)arrayAccess.Array.Type!).ElementType.ToTypeSignature().ToTypeDefOrRef());
                break;
            case FieldReference field:
                if (field.Field.IsStatic)
                {
                    instructions.Add(CilOpCodes.Ldsfld, field.Field.ToFieldDescriptor());
                    break;
                }

                LoadLocal(field.Local, method, locals);
                instructions.Add(CilOpCodes.Ldfld, field.Field.ToFieldDescriptor());
                break;
            case MemoryOperand memory:
                if (memory.Index == null && memory.Addend == 0 && memory.Scale == 0
                    && memory.Base is LocalVariable { Type: ByRefTypeAnalysisContext { ElementType: { } referent } } local2)
                {
                    LoadLocal(local2, method, locals);
                    instructions.Add(CilOpCodes.Ldobj, referent.ToTypeSignature().ToTypeDefOrRef());
                    break;
                }
                throw new DecompilerException($"Memory load is unresolved: {operand}");
            case RuntimeMethodInfoAnalysisContext runtimeMethod:
                // A delegate constructor takes its target as a native pointer, which is exactly ldftn.
                if (expectedType?.FullName == "System.IntPtr")
                {
                    instructions.Add(CilOpCodes.Ldftn, runtimeMethod.RepresentedMethod.ToMethodDescriptor());
                    break;
                }

                throw new DecompilerException("Runtime method metadata survived resolution");
            case RuntimeFieldInfoAnalysisContext runtimeField:
                // fieldof(F), e.g. the handle InitializeArray takes.
                if (expectedType?.FullName == "System.RuntimeFieldHandle")
                {
                    instructions.Add(CilOpCodes.Ldtoken, runtimeField.RepresentedField.ToFieldDescriptor());
                    break;
                }

                throw new DecompilerException("Runtime field metadata survived resolution");
            case RuntimeClassTypeAnalysisContext or RgctxTableTypeAnalysisContext
                or MethodRgctxTableTypeAnalysisContext or StaticFieldStorageTypeAnalysisContext:
                throw new DecompilerException("Runtime type or generic-context metadata survived resolution");
            case TypeAnalysisContext type:
                //typeof(T)
                var corLibScope = module.CorLibTypeFactory.CorLibScope;
                var typeFromHandle = corLibScope
                    .CreateTypeReference("System", "Type")
                    .CreateMemberReference("GetTypeFromHandle", MethodSignature.CreateStatic(
                        corLibScope.CreateTypeReference("System", "Type").ToTypeSignature(false),
                        [corLibScope.CreateTypeReference("System", "RuntimeTypeHandle").ToTypeSignature(true)]));

                instructions.Add(CilOpCodes.Ldtoken, type.ToTypeSignature().ToTypeDefOrRef());
                instructions.Add(CilOpCodes.Call, typeFromHandle);
                break;
            default:
                throw new DecompilerException($"Unsupported operand: {operand}");
        }
    }
    
    private static bool TryEmitExactTypeComparison(Instruction instruction, MethodDefinition method,
        EmissionLocals locals)
    {
        var left = instruction.Operands[1];
        var right = instruction.Operands[2];

        IOperand typeOperand;
        LocalVariable objLocal;

        if (left is TypeAnalysisContext && IsKlassPointerLoad(right, out var rightLocal))
            (typeOperand, objLocal) = (left, rightLocal);
        else if (right is TypeAnalysisContext && IsKlassPointerLoad(left, out var leftLocal))
            (typeOperand, objLocal) = (right, leftLocal);
        else
            return false;

        var module = method.DeclaringModule!;
        var instructions = method.CilMethodBody!.Instructions;

        var getType = module.CorLibTypeFactory.CorLibScope
            .CreateTypeReference("System", "Object")
            .CreateMemberReference("GetType", MethodSignature.CreateInstance(
                module.CorLibTypeFactory.CorLibScope.CreateTypeReference("System", "Type").ToTypeSignature(false)));

        LoadLocal(objLocal, method, locals);
        instructions.Add(CilOpCodes.Callvirt, getType);
        LoadOperand(typeOperand, method, locals); // emits typeof(T)
        instructions.Add(CilOpCodes.Ceq);

        if (instruction.OpCode == OpCode.CheckNotEqual)
        {
            instructions.Add(CilOpCodes.Ldc_I4_0);
            instructions.Add(CilOpCodes.Ceq);
        }

        StoreToOperand(instruction.Operands[0], method, locals);
        return true;
    }
    
    private static bool IsKlassPointerLoad(IOperand operand, out LocalVariable local)
    {
        if (operand is MemoryOperand { Index: null, Addend: 0, Scale: 0, Base: LocalVariable { Type.IsValueType: false } baseLocal })
        {
            local = baseLocal;
            return true;
        }

        local = null!;
        return false;
    }

    private static bool IsBoolean(IOperand operand, MethodAnalysisContext context) =>
        operand is LocalVariable { Type: { } type } && type == context.AppContext.SystemTypes.SystemBooleanType;

    private static bool IsZeroConstant(IOperand operand) => operand is Immediate { Value: 0 };
    
    private static TypeAnalysisContext? DestinationType(IOperand destination) =>
        destination switch
        {
            LocalVariable local => local.Type,
            FieldReference field => field.Field.FieldType,
            ArrayAccess { Array.Type: SzArrayTypeAnalysisContext array } => array.ElementType,
            MemoryOperand { Base: LocalVariable { Type: ByRefTypeAnalysisContext byRef }, Index: null, Addend: 0, Scale: 0 } => byRef.ElementType,
            _ => null
        };

    private static void LoadLocal(LocalVariable local, MethodDefinition method, EmissionLocals locals)
    {
        var instructions = method.CilMethodBody!.Instructions;

        if (local.IsMethodInfo)
            throw new DecompilerException("Hidden method metadata survived operand resolution");
        if (locals.Parameters.TryGetValue(local, out var parameter))
            instructions.Add(CilOpCodes.Ldarg, parameter);
        else
            instructions.Add(CilOpCodes.Ldloc, locals[local]);
    }

    private static void StoreToOperand(IOperand operand, MethodDefinition method,
        EmissionLocals locals)
    {
        var instructions = method.CilMethodBody!.Instructions;

        switch (operand)
        {
            case LocalVariable local:
                if (locals.Parameters.TryGetValue(local, out var parameter))
                    instructions.Add(CilOpCodes.Starg, parameter);
                else
                    instructions.Add(CilOpCodes.Stloc, locals[local]);
                break;

            case FieldReference field:
                var fieldDescriptor = field.Field.ToFieldDescriptor();

                if (field.Field.IsStatic)
                {
                    instructions.Add(CilOpCodes.Stsfld, fieldDescriptor);
                    break;
                }

                // stfld wants the object underneath the value, but the value is already on the stack, so
                // park it in a temporary while we load the object.
                var scratch = new CilLocalVariable(fieldDescriptor.Signature!.FieldType);
                method.CilMethodBody!.LocalVariables.Add(scratch);

                instructions.Add(CilOpCodes.Stloc, scratch);
                LoadLocal(field.Local, method, locals);
                instructions.Add(CilOpCodes.Ldloc, scratch);
                instructions.Add(CilOpCodes.Stfld, fieldDescriptor);
                break;

            case ArrayAccess arrayAccess:
                // stelem needs array and index before the value, so the same trick as stfld
                var elementType = ((SzArrayTypeAnalysisContext)arrayAccess.Array.Type!).ElementType;
                var elementScratch = new CilLocalVariable(elementType.ToTypeSignature());
                method.CilMethodBody!.LocalVariables.Add(elementScratch);

                instructions.Add(CilOpCodes.Stloc, elementScratch);
                LoadLocal(arrayAccess.Array, method, locals);
                LoadOperand(arrayAccess.Index, method, locals);
                instructions.Add(CilOpCodes.Ldloc, elementScratch);
                instructions.Add(CilOpCodes.Stelem, elementType.ToTypeSignature().ToTypeDefOrRef());
                break;

            case MemoryOperand memory:
                if (memory.Index == null && memory.Addend == 0 && memory.Scale == 0
                    && memory.Base is LocalVariable { Type: ByRefTypeAnalysisContext { ElementType: { } referent } } local2)
                {
                    var storedValue = new CilLocalVariable(referent.ToTypeSignature());
                    method.CilMethodBody.LocalVariables.Add(storedValue);
                    instructions.Add(CilOpCodes.Stloc, storedValue);
                    LoadLocal(local2, method, locals);
                    instructions.Add(CilOpCodes.Ldloc, storedValue);
                    instructions.Add(CilOpCodes.Stobj, referent.ToTypeSignature().ToTypeDefOrRef());
                    break;
                }
                throw new DecompilerException($"Memory store is unresolved: {operand}");

            default:
                throw new DecompilerException($"Store destination is unresolved: {operand}");
        }
    }
}
