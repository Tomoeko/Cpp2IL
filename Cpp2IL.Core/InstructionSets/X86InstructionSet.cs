using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Il2CppApiFunctions;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;

namespace Cpp2IL.Core.InstructionSets;

// This is honestly an X64InstructionSet by all means. Everything here screams "I AM X64".
public class X86InstructionSet : Cpp2IlInstructionSet
{
    private static readonly MasmFormatter Formatter = new();
    private static readonly StringOutput Output = new();
    private static readonly X64CallingConventionResolver CallingConventions = new();

    public override BaseCallingConventionResolver CallingConventionResolver => CallingConventions;

    private static ISIL.Immediate Imm(long value) => new(value);
    private static ISIL.Immediate Imm(ulong value) => new(unchecked((long)value));

    internal static bool TargetsOutsideMethod(ulong target, ulong methodStart, int bodyLength) =>
        target < methodStart || target - methodStart >= (ulong)bodyLength;

    private static string FormatInstructionInternal(Instruction instruction)
    {
        Formatter.Format(instruction, Output);
        return Output.ToStringAndReset();
    }

    public static string FormatInstruction(Instruction instruction)
    {
        lock (Formatter)
        {
            return FormatInstructionInternal(instruction);
        }
    }

    public override BinarySlice GetRawBytesForMethod(MethodAnalysisContext context, bool isAttributeGenerator) => X86Utils.GetRawManagedOrCaCacheGenMethodBody(context.UnderlyingPointer, isAttributeGenerator, context.AppContext);

    public override BaseKeyFunctionAddresses CreateKeyFunctionAddressesInstance() => new X86KeyFunctionAddresses();

    public override string PrintAssembly(MethodAnalysisContext context)
    {
        lock (Formatter)
        {
            var insns = X86Utils.Iterate(context);

            return string.Join("\n", insns.Select(FormatInstructionInternal));
        }
    }

    public override List<ISIL.Instruction> GetIsilFromMethod(MethodAnalysisContext context)
    {
        var instructions = new List<ISIL.Instruction>();
        var addresses = new List<ulong>();

        var nativeInstructions = X86Utils.Iterate(context).ToArray();
        var noReturnCalls = new HashSet<ulong>();
        if (X64ReferenceFieldStoreProof.TryLift(context, nativeInstructions) is { } referenceStore)
            return referenceStore; // The closed proof includes the null and GC helper paths.
        if (X86ByteThresholdReturnProof.TryLift(context, nativeInstructions) is { } byteThreshold)
            return byteThreshold; // The complete leaf proves one unsigned byte-field predicate.
        if (X86IntegerArrayAccessProof.TryLift(context, nativeInstructions) is { } arrayAccess)
            return arrayAccess; // The closed proof includes both helper exits and the caller unwind region.
        if (X86IntegerExtensionProof.TryLift(context, nativeInstructions) is { } integerExtension)
            return QualifyExceptionRegions(integerExtension);
        if (X86ScalarTruncationProof.TryLift(context, nativeInstructions) is { } scalarTruncation)
            return QualifyExceptionRegions(scalarTruncation);
        var referenceNullReturn = X86ReferenceNullReturnProof.IsApplicable(context, nativeInstructions);
        var booleanReturnSelfTests = X86BooleanReturnSelfTestProof.Find(context, nativeInstructions);
        var nonvolatileXmmTraffic = X86NonvolatileXmmStackProof.Find(context, nativeInstructions);
        var singleWidthDividends = X86DivisionProof.FindSingleWidthDividends(nativeInstructions);
        var shiftCountExtensions = X86ShiftCountExtensionProof.Find(context, nativeInstructions);
        var metadataGuard = X86MetadataGuardProof.Find(context, nativeInstructions);
        var unresolvedMetadataGuards = X86MetadataGuardProof.FindUnresolvedInitializationGuards(context, nativeInstructions);
        if (X86GuardedZeroStoreProof.Find(context, nativeInstructions) is { } guardedZeroStore)
            context.PutExtraData(X86GuardedZeroStoreProof.EvidenceKey, guardedZeroStore);
        if (X86BooleanFieldReadProof.Find(context, nativeInstructions) is { } booleanFieldRead)
            context.PutExtraData(X86BooleanFieldReadProof.EvidenceKey, booleanFieldRead);
        if (metadataGuard != null)
            context.PutExtraData("X86MetadataLiteralGuardProof", metadataGuard);
        foreach (var instruction in nativeInstructions)
        {
            if (metadataGuard?.RemovedAddresses.Contains(instruction.IP) == true)
                continue;
            var firstLiftedIndex = instructions.Count;
            if (referenceNullReturn && instruction.IP == context.UnderlyingPointer)
            {
                // This complete leaf uses only ZF from TEST. Keep the original
                // managed reference as the zero predicate's operand; integer
                // flag arithmetic would turn it into an invalid managed scalar.
                addresses.Add(instruction.IP);
                instructions.Add(new ISIL.Instruction(instructions.Count, ISIL.OpCode.CheckEqual,
                    new ISIL.Register(null, "ZF"), new ISIL.Register(null, "rcx"), Imm(0))
                    { IntegerBitWidth = 64 });
            }
            else if (booleanReturnSelfTests.Contains(instruction.IP))
            {
                // The adjacent managed call defines a Boolean result in AL. Only its zero
                // predicate is proved; native RAX's unused upper bits and TEST's PF/SF are
                // deliberately not represented. The proof excludes every other live flag use.
                addresses.Add(instruction.IP);
                instructions.Add(new ISIL.Instruction(instructions.Count, ISIL.OpCode.CheckEqual,
                    new ISIL.Register(null, "ZF"), new ISIL.Register(null, "rax"), Imm(0)));
            }
            else if (nonvolatileXmmTraffic.Contains(instruction.IP))
            {
                // These two native operations only preserve a nonvolatile register
                // across this method's ABI boundary. Their 128-bit payload is not
                // interpreted as a managed scalar or vector value.
                addresses.Add(instruction.IP);
                instructions.Add(new ISIL.Instruction(instructions.Count, ISIL.OpCode.Nop));
            }
            else
                ConvertInstructionStatement(instruction, instructions, addresses, context,
                    singleWidthDividends.Contains(instruction.IP), shiftCountExtensions.Contains(instruction.IP),
                    unresolvedMetadataGuards.Contains(instruction.IP));
            if (instruction.Code == Code.Call_rel32_64 &&
                instructions.Skip(firstLiftedIndex).Any(lifted => lifted.OpCode == ISIL.OpCode.RuntimeNullThrow))
                noReturnCalls.Add(instruction.IP);
        }

        if (X86CallerExceptionRegionProof.Check(context, nativeInstructions, noReturnCalls) is { } exceptionRegionFailure)
            return [new(0, ISIL.OpCode.NotImplemented, new ISIL.StringLiteral(
                X86TerminalBoundsThrowDiagnostic.TryClassify(context, nativeInstructions,
                    noReturnCalls, exceptionRegionFailure) ?? exceptionRegionFailure))];
        X86BodyBoundary.AppendFallthroughFailure(instructions);

        // fix branches
        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];

            if (instruction.OpCode != ISIL.OpCode.Jump && instruction.OpCode != ISIL.OpCode.ConditionalJump)
                continue;

            var targetAddress = ((ISIL.Immediate)instruction.Operands[0]).UnsignedValue;
            var targetIndex = addresses.FindIndex(addr => addr == targetAddress);

            if (targetIndex == -1)
            {
                instruction.OpCode = ISIL.OpCode.Invalid;
                instruction.SetOperands(new ISIL.StringLiteral($"Jump target not found in method: 0x{targetAddress:X4}"));
                continue;
            }

            var targetInstruction = instructions[targetIndex];

            instruction.SetOperand(0, targetInstruction);
        }

        return instructions;

        List<ISIL.Instruction> QualifyExceptionRegions(List<ISIL.Instruction> lifted)
            => X86CallerExceptionRegionProof.Check(context, nativeInstructions, noReturnCalls) is { } failure
                ? [new(0, ISIL.OpCode.NotImplemented, new ISIL.StringLiteral(failure))]
                : lifted;
    }

    private static ISIL.Register? ReturnRegisterClobberedBy(MethodAnalysisContext callee)
        => callee.IsVoid ? CallingConventions.ReturnRegister(callee) : null;

    public override List<ISIL.IOperand> GetParameterOperandsFromMethod(MethodAnalysisContext context)
    {
        return CallingConventions.ResolveForManaged(context).ToList();
    }

    public override ulong GetThunkTarget(ApplicationAnalysisContext context, ulong thunkAddress)
    {
        var binary = context.Binary;

        if (!binary.TryMapVirtualAddressToRaw(thunkAddress, out var rawAddress))
            return 0;

        var raw = binary.GetRawBinaryContent();
        var length = (int)Math.Min(32, raw.Length - rawAddress);
        if (length <= 0)
            return 0;

        var decoder = Decoder.Create(binary.is32Bit ? 32 : 64, new ByteArrayCodeReader(raw.Slice((int)rawAddress, length).ToArray()), thunkAddress);
        
        for (var i = 0; i < 4; i++)
        {
            var instruction = decoder.Decode();

            if (instruction.FlowControl == FlowControl.UnconditionalBranch && instruction.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64)
                return instruction.NearBranchTarget;

            if (instruction.FlowControl != FlowControl.Next)
                return 0;
        }

        return 0;
    }

    public override ulong GetInternalCallTarget(MethodAnalysisContext method)
    {
        var start = GetPointerForMethod(method);
        var length = method.RawBytes.Length;

        if (start == 0 || length == 0)
            return 0;

        var decoder = Decoder.Create(method.AppContext.Binary.is32Bit ? 32 : 64, new ByteArrayCodeReader(method.RawBytes.ToArray()), start);
        var target = 0ul;

        while (decoder.IP < start + (ulong)length)
        {
            var instruction = decoder.Decode();

            if (instruction.FlowControl != FlowControl.UnconditionalBranch || instruction.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64))
                continue;

            var branch = instruction.NearBranchTarget;

            if (branch >= start && branch < start + (ulong)length)
                continue; // ordinary control flow within the stub

            if (target != 0 && target != branch)
                return 0;

            target = branch;
        }

        return target;
    }

    public override (IReadOnlyList<ulong> DataReferences, IReadOnlyList<ulong> CallTargets) InspectPotentialThrowHelper(ApplicationAnalysisContext context, ulong address)
    {
        Iced.Intel.InstructionList body;
        try
        {
            body = X86Utils.GetMethodBodyAtVirtAddressNew(address, true, context);
        }
        catch
        {
            return ([], []);
        }

        var dataReferences = new List<ulong>();
        var callTargets = new List<ulong>();

        foreach (var insn in body)
        {
            if (insn.Mnemonic == Mnemonic.Lea && insn.IsIPRelativeMemoryOperand)
                dataReferences.Add(insn.IPRelativeMemoryAddress);
            else if (insn.Mnemonic == Mnemonic.Call && insn.Op0Kind == OpKind.NearBranch64)
                callTargets.Add(insn.NearBranchTarget);
        }

        return (dataReferences, callTargets);
    }

    internal List<ISIL.Instruction> GetIsilFromInstruction(Instruction instruction, MethodAnalysisContext? context = null)
    {
        var instructions = new List<ISIL.Instruction>();
        ConvertInstructionStatement(instruction, instructions, [], context!);
        return instructions;
    }

    private static readonly (RflagsBits Flag, string Name)[] StatusFlags =
    [
        (RflagsBits.CF, "CF"), (RflagsBits.PF, "PF"), (RflagsBits.AF, "AF"),
        (RflagsBits.ZF, "ZF"), (RflagsBits.SF, "SF"), (RflagsBits.OF, "OF"),
    ];

    private void ConvertInstructionStatement(Instruction instruction, List<ISIL.Instruction> instructions, List<ulong> addresses, MethodAnalysisContext context, bool singleWidthDividend = false, bool shiftCountExtension = false, bool unresolvedMetadataGuard = false)
    {
        var first = instructions.Count;
        ConvertInstructionStatementCore(instruction, instructions, addresses, context, singleWidthDividend,
            shiftCountExtension, unresolvedMetadataGuard);

        // CALL itself does not change RFLAGS, but the ABI does not preserve status flags across
        // its opaque callee. Tail jumps have no returning continuation in the current method.
        var opaqueCall = instruction.Mnemonic == Mnemonic.Call;
        var modified = opaqueCall
            ? RflagsBits.CF | RflagsBits.PF | RflagsBits.AF | RflagsBits.ZF | RflagsBits.SF | RflagsBits.OF
            : instruction.RflagsModified;
        if (modified == RflagsBits.None)
            return;

        var emittedFlags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = first; index < instructions.Count; index++)
        {
            var emitted = instructions[index];
            if (emitted.OpCode is ISIL.OpCode.Invalid or ISIL.OpCode.NotImplemented)
                return; // The operation already rejects recovery independently of its flag results.
            if (emitted.Destination is ISIL.Register destination)
                emittedFlags.Add(destination.Name);
        }

        var conditionalFlags = instruction.Mnemonic is Mnemonic.Shl or Mnemonic.Sal or Mnemonic.Shr or Mnemonic.Sar
            or Mnemonic.Shld or Mnemonic.Shrd or Mnemonic.Rol or Mnemonic.Ror or Mnemonic.Rcl or Mnemonic.Rcr;
        if (conditionalFlags)
        {
            // Immediate counts can prove that no flags change. A variable count can be zero,
            // so never treat its "cleared" flag metadata as an unconditional constant.
            var countIndex = instruction.OpCount - 1;
            if (countIndex > 0 && instruction.GetOpKind(countIndex).IsImmediate())
            {
                var width = instruction.Op0Kind == OpKind.Register ? instruction.Op0Register.GetSize() : instruction.MemorySize.GetSize();
                if ((instruction.GetImmediate(countIndex) & (width == 8 ? 63UL : 31UL)) == 0)
                    modified = RflagsBits.None;
            }
        }

        foreach (var (flag, name) in StatusFlags)
        {
            if ((modified & flag) == 0 || emittedFlags.Contains(name))
                continue;
            var destination = new ISIL.Register(null, name);
            ISIL.Instruction definition;
            if (!opaqueCall && !conditionalFlags && (instruction.RflagsCleared & flag) != 0)
                definition = new(instructions.Count, ISIL.OpCode.Move, destination, Imm(0));
            else if (!opaqueCall && !conditionalFlags && (instruction.RflagsSet & flag) != 0)
                definition = new(instructions.Count, ISIL.OpCode.Move, destination, Imm(1));
            else
            {
                var reason = opaqueCall ? $"Opaque call does not preserve {name}."
                    : (instruction.RflagsUndefined & flag) != 0 ? $"Native {instruction.Mnemonic} leaves {name} undefined."
                    : $"Native {instruction.Mnemonic} {name} result is not recovered.";
                definition = new(instructions.Count, ISIL.OpCode.UnresolvedValue, destination, new ISIL.StringLiteral(reason));
            }
            addresses.Add(instruction.IP);
            instructions.Add(definition);
        }
    }

    private void ConvertInstructionStatementCore(Instruction instruction, List<ISIL.Instruction> instructions, List<ulong> addresses, MethodAnalysisContext context, bool singleWidthDividend, bool shiftCountExtension, bool unresolvedMetadataGuard)
    {
        var callNoReturn = false;
        int operandSize;

        ISIL.Instruction Add(ulong address, ISIL.OpCode opCode, params List<ISIL.IOperand> operands)
        {
            addresses.Add(address);
            var newInstruction = new ISIL.Instruction(instructions.Count, opCode, operands);
            instructions.Add(newInstruction);
            return newInstruction;
        }

        // Preserve all the argument registers as we don't know which ones are used
        void AddIndirectCall(Instruction source)
        {
            var call = Add(source.IP, ISIL.OpCode.IndirectCall, ConvertOperand(source, 0), new ISIL.Register(null, "rax") /* return value */);
            call.AddOperands(CallingConventions.ResolveForUnmanaged(context.AppContext, source.IP));
        }
        
        // Preserve all the argument registers as we don't know which ones are used
        void AddIndirectJmp(Instruction source)
        {
            var call = Add(source.IP, ISIL.OpCode.IndirectJump, ConvertOperand(source, 0), new ISIL.Register(null, "rax") /* return value */);
            call.AddOperands(CallingConventions.ResolveForUnmanaged(context.AppContext, source.IP));
        }

        switch (instruction.Mnemonic)
        {
            case Mnemonic.Mov:
                Add(instruction.IP, ISIL.OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                break;
            case Mnemonic.Movd:
            case Mnemonic.Movq:
            case Mnemonic.Movaps:
            case Mnemonic.Movups:
            case Mnemonic.Movdqa:
            case Mnemonic.Movdqu:
            case Mnemonic.Shufps:
            case Mnemonic.Unpcklps:
            case Mnemonic.Andps:
            case Mnemonic.Orps:
            case Mnemonic.Xorps:
                // Raw-bit transfers, packed lanes and full-width memory accesses are not
                // ordinary typed scalar moves/arithmetic. Even self-XOR requires an
                // independently proved scalar projection before emitting a managed zero.
                Add(instruction.IP, ISIL.OpCode.NotImplemented,
                    new ISIL.StringLiteral("SIMD operation requires proved bit, lane and memory-width semantics: " + FormatInstruction(instruction)));
                break;
            case Mnemonic.Cvtdq2ps:
            case Mnemonic.Cvtps2pd:
            case Mnemonic.Cvtdq2pd:
            case Mnemonic.Cvtpd2ps:
            case Mnemonic.Cvttsd2si:
                // Numeric conversion changes representation, precision, rounding or lane
                // width. An ordinary Move cannot preserve its semantics or exceptional cases.
                Add(instruction.IP, ISIL.OpCode.NotImplemented,
                    new ISIL.StringLiteral("Numeric conversion requires proved width, rounding and lane semantics: " + FormatInstruction(instruction)));
                break;
            case Mnemonic.Movss: // scalar single - as a move, but a load from a constant address is a float literal
            case Mnemonic.Movsd: // scalar double
                Add(instruction.IP, ISIL.OpCode.Move, ConvertOperand(instruction, 0), ConvertScalarFloatOperand(instruction, 1, instruction.Mnemonic == Mnemonic.Movss, context));
                break;
            case Mnemonic.Movzx:
                if (context != null && context.GetExtraData<X86BooleanFieldReadProof.Proof>(
                        X86BooleanFieldReadProof.EvidenceKey) is { } fieldRead &&
                    instruction.IP == fieldRead.LoadIp)
                {
                    Add(instruction.IP, ISIL.OpCode.Move, ConvertOperand(instruction, 0),
                        ConvertOperand(instruction, 1));
                    break;
                }
                if (shiftCountExtension)
                {
                    // The source is a proved Int32 parameter (possibly explicitly masked),
                    // and the result is read only through CL by 32/64-bit register shifts.
                    Add(instruction.IP, ISIL.OpCode.IntegerExtend, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1),
                        Imm(8), Imm(32), Imm(0));
                    break;
                }
                goto case Mnemonic.Movsx;
            case Mnemonic.Movsx:
            case Mnemonic.Movsxd:
            case Mnemonic.Cbw:
            case Mnemonic.Cwde:
            case Mnemonic.Cdqe:
            case Mnemonic.Cwd:
                // Treating extension as Move loses source truncation, signedness and the
                // different upper-bit effects of 16/32/64-bit destination writes. Only the
                // separate native proofs may lower a supported extension sequence.
                Add(instruction.IP, ISIL.OpCode.NotImplemented,
                    new ISIL.StringLiteral("Integer extension requires proved source bits and destination register semantics: " + FormatInstruction(instruction)));
                break;
            case Mnemonic.Cdq: // EDX:EAX := sign-extend EAX
                Add(instruction.IP, ISIL.OpCode.ShiftRight, new ISIL.Register(null, X86Utils.GetRegisterName(Register.EDX)),
                    new ISIL.Register(null, X86Utils.GetRegisterName(Register.EAX)), Imm(31)).IntegerBitWidth = 32;
                break;
            case Mnemonic.Cqo: // RDX:RAX := sign-extend RAX
                Add(instruction.IP, ISIL.OpCode.ShiftRight, new ISIL.Register(null, X86Utils.GetRegisterName(Register.RDX)),
                    new ISIL.Register(null, X86Utils.GetRegisterName(Register.RAX)), Imm(63)).IntegerBitWidth = 64;
                break;
            case Mnemonic.Lea:
                var destination = ConvertOperand(instruction, 0);
                var leaWidth = instruction.Op0Register.GetSize() * 8;
                if (leaWidth is not (32 or 64))
                {
                    Add(instruction.IP, ISIL.OpCode.NotImplemented, new ISIL.StringLiteral("LEA requires a supported 32/64-bit result width"));
                    return;
                }

                // RIP-relative LEA is effectively loading the absolute address.
                if (instruction.IsIPRelativeMemoryOperand)
                {
                    Add(instruction.IP, ISIL.OpCode.Move, destination, Imm((long)instruction.IPRelativeMemoryAddress));
                    return;
                }

                // Stack-address LEA keeps stack semantics represented as address-of stack slot.
                if (instruction is { MemoryBase: Register.RSP, MemoryIndex: Register.None })
                {
                    Add(instruction.IP, ISIL.OpCode.Move, destination, ConvertOperand(instruction, 1, true));
                    return;
                }

                // Absolute-address LEA also computes a value rather than loading from memory.
                if (instruction.MemoryBase == Register.None && instruction.MemoryIndex == Register.None)
                {
                    Add(instruction.IP, ISIL.OpCode.Move, destination, Imm((long)instruction.MemoryDisplacement64));
                    return;
                }

                if (instruction.MemoryIndex != Register.None)
                {
                    ISIL.IOperand? baseRegister = instruction.MemoryBase != Register.None
                        ? new ISIL.Register(null, X86Utils.GetRegisterName(instruction.MemoryBase))
                        : null;
                    var indexRegister = new ISIL.Register(null, X86Utils.GetRegisterName(instruction.MemoryIndex));
                    var source = (ISIL.IOperand)indexRegister;

                    if (instruction.MemoryIndexScale > 1)
                    {
                        if (baseRegister != null)
                        {
                            var temp = new ISIL.Register(null, "TEMP");
                            Add(instruction.IP, ISIL.OpCode.Multiply, temp, indexRegister, Imm(instruction.MemoryIndexScale)).IntegerBitWidth = leaWidth;
                            source = temp;
                        }
                        else
                        {
                            Add(instruction.IP, ISIL.OpCode.Multiply, destination, indexRegister, Imm(instruction.MemoryIndexScale)).IntegerBitWidth = leaWidth;
                            source = destination;
                        }
                    }

                    if (baseRegister != null)
                        Add(instruction.IP, ISIL.OpCode.Add, destination, baseRegister, source).IntegerBitWidth = leaWidth;
                    else if (!ReferenceEquals(source, destination))
                        Add(instruction.IP, ISIL.OpCode.Move, destination, source);

                    var displacement = unchecked((long)instruction.MemoryDisplacement64);
                    if (displacement > 0)
                        Add(instruction.IP, ISIL.OpCode.Add, destination, destination, Imm(displacement)).IntegerBitWidth = leaWidth;
                    else if (displacement < 0)
                        Add(instruction.IP, ISIL.OpCode.Subtract, destination, destination, Imm(-displacement)).IntegerBitWidth = leaWidth;

                    return;
                }

                if (instruction.MemoryBase != Register.None && instruction.MemoryBase != Register.RSP)
                {
                    var baseRegister = new ISIL.Register(null, X86Utils.GetRegisterName(instruction.MemoryBase));
                    var displacement = unchecked((long)instruction.MemoryDisplacement64);

                    if (displacement == 0)
                        Add(instruction.IP, ISIL.OpCode.Move, destination, baseRegister);
                    else if (displacement > 0)
                        Add(instruction.IP, ISIL.OpCode.Add, destination, baseRegister, Imm(displacement)).IntegerBitWidth = leaWidth;
                    else
                        Add(instruction.IP, ISIL.OpCode.Subtract, destination, baseRegister, Imm(-displacement)).IntegerBitWidth = leaWidth;

                    return;
                }

                Add(instruction.IP, ISIL.OpCode.Move, destination, ConvertOperand(instruction, 1, true));
                break;
            case Mnemonic.Xor:
                if (instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register && instruction.Op0Register == instruction.Op1Register)
                    Add(instruction.IP, ISIL.OpCode.Move, ConvertOperand(instruction, 0), Imm(0));
                else
                    Add(instruction.IP, ISIL.OpCode.Xor, ConvertOperand(instruction, 0), ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                break;
            case Mnemonic.Shl:
            case Mnemonic.Sal:
            case Mnemonic.Shr:
            case Mnemonic.Sar:
                var shiftWidth = (instruction.Op0Kind == OpKind.Register ? instruction.Op0Register.GetSize() : instruction.MemorySize.GetSize()) * 8;
                if (shiftWidth is not (32 or 64))
                {
                    Add(instruction.IP, ISIL.OpCode.NotImplemented, new ISIL.StringLiteral("Shift requires a supported 32/64-bit operand width"));
                    break;
                }
                var shiftOpcode = instruction.Mnemonic switch
                {
                    Mnemonic.Shr => ISIL.OpCode.ShiftRightUnsigned,
                    Mnemonic.Sar => ISIL.OpCode.ShiftRight,
                    _ => ISIL.OpCode.ShiftLeft,
                };
                Add(instruction.IP, shiftOpcode, ConvertOperand(instruction, 0), ConvertOperand(instruction, 0), ConvertOperand(instruction, 1)).IntegerBitWidth = shiftWidth;
                break;
            case Mnemonic.And:
                Add(instruction.IP, ISIL.OpCode.And, ConvertOperand(instruction, 0), ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                break;
            case Mnemonic.Or:
                Add(instruction.IP, ISIL.OpCode.Or, ConvertOperand(instruction, 0), ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                break;
            case Mnemonic.Bts: // CF = old bit, then set it
                {
                    var dest = ConvertOperand(instruction, 0);
                    var bit = ConvertOperand(instruction, 1);
                    var temp = new ISIL.Register(null, "TEMP");
                    Add(instruction.IP, ISIL.OpCode.ShiftRight, temp, dest, bit);
                    Add(instruction.IP, ISIL.OpCode.And, new ISIL.Register(null, "CF"), temp, Imm(1));
                    Add(instruction.IP, ISIL.OpCode.ShiftLeft, temp, Imm(1), bit);
                    Add(instruction.IP, ISIL.OpCode.Or, dest, dest, temp);
                    break;
                }
            case Mnemonic.Btr: // CF = old bit, then clear it
                {
                    var dest = ConvertOperand(instruction, 0);
                    var bit = ConvertOperand(instruction, 1);
                    var temp = new ISIL.Register(null, "TEMP");
                    Add(instruction.IP, ISIL.OpCode.ShiftRight, temp, dest, bit);
                    Add(instruction.IP, ISIL.OpCode.And, new ISIL.Register(null, "CF"), temp, Imm(1));
                    Add(instruction.IP, ISIL.OpCode.ShiftLeft, temp, Imm(1), bit);
                    Add(instruction.IP, ISIL.OpCode.Not, temp, temp); // temp = ~(1 << bit)
                    Add(instruction.IP, ISIL.OpCode.And, dest, dest, temp);
                    break;
                }
            case Mnemonic.Not:
                Add(instruction.IP, ISIL.OpCode.Not, ConvertOperand(instruction, 0), ConvertOperand(instruction, 0));
                break;
            case Mnemonic.Neg: // dest := -dest
                Add(instruction.IP, ISIL.OpCode.Negate, ConvertOperand(instruction, 0), ConvertOperand(instruction, 0));
                break;
            case Mnemonic.Imul:
                if (instruction.OpCount == 1)
                {
                    int opSize = instruction.Op0Kind == OpKind.Register ? instruction.Op0Register.GetSize() : instruction.MemorySize.GetSize();
                    switch (opSize) // TODO: I don't know how to work with dual registers here, I left hints though
                    {
                        case 1: // Op0 * AL -> AX
                            Add(instruction.IP, ISIL.OpCode.Multiply, Register.AX.MakeIndependent(), ConvertOperand(instruction, 0), Register.AL.MakeIndependent());
                            return;
                        case 2: // Op0 * AX -> DX:AX

                            break;
                        case 4: // Op0 * EAX -> EDX:EAX

                            break;
                        case 8: // Op0 * RAX -> RDX:RAX

                            break;
                        default: // prob 0, I think fallback to architecture alignment would be good here(issue: idk how to find out arch alignment)

                            break;
                    }

                    // if got to here, it didn't work
                    goto default;
                }
                else if (instruction.OpCount == 3) Add(instruction.IP, ISIL.OpCode.Multiply, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), ConvertOperand(instruction, 2));
                else Add(instruction.IP, ISIL.OpCode.Multiply, ConvertOperand(instruction, 0), ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));

                break;
            case Mnemonic.Idiv:
            case Mnemonic.Div:
                {
                    var divisorSize = instruction.Op0Kind == OpKind.Register ? instruction.Op0Register.GetSize() : instruction.MemorySize.GetSize();

                    if (instruction.Op0Kind != OpKind.Register || divisorSize is not (4 or 8) || !singleWidthDividend)
                    {
                        Add(instruction.IP, ISIL.OpCode.NotImplemented,
                            new ISIL.StringLiteral("Native integer division requires a register divisor and a proved 32/64-bit single-width dividend"));
                        break;
                    }

                    // The decoded basic-block proof established zero or matching sign extension
                    // in the high half. Snapshot the divisor before overwriting either output;
                    // it may itself use RAX/RDX. Memory divisors still require read-width proof.
                    var quotient = new ISIL.Register(null, X86Utils.GetRegisterName(Register.RAX));
                    var remainder = new ISIL.Register(null, X86Utils.GetRegisterName(Register.RDX));

                    var dividend = new ISIL.Register(null, "TEMP_DIVIDEND");
                    var divisor = new ISIL.Register(null, "TEMP_DIVISOR");

                    Add(instruction.IP, ISIL.OpCode.Move, dividend, quotient);
                    Add(instruction.IP, ISIL.OpCode.Move, divisor, ConvertOperand(instruction, 0));
                    var signedDivision = instruction.Mnemonic == Mnemonic.Idiv;
                    Add(instruction.IP, signedDivision ? ISIL.OpCode.Divide : ISIL.OpCode.DivideUnsigned,
                        quotient, dividend, divisor).IntegerBitWidth = divisorSize * 8;
                    // Keep the quotient even when only the remainder is used: IDIV traps on
                    // signed quotient overflow, whereas a managed remainder alone need not.
                    Add(instruction.IP, signedDivision ? ISIL.OpCode.Modulo : ISIL.OpCode.ModuloUnsigned,
                        remainder, dividend, divisor).IntegerBitWidth = divisorSize * 8;
                    break;
                }
            case Mnemonic.Mulss:
            case Mnemonic.Vmulss:
                if (instruction.OpCount == 3)
                    Add(instruction.IP, ISIL.OpCode.Multiply, ConvertOperand(instruction, 0), ConvertScalarFloatOperand(instruction, 1, true, context), ConvertScalarFloatOperand(instruction, 2, true, context));
                else if (instruction.OpCount == 2)
                    Add(instruction.IP, ISIL.OpCode.Multiply, ConvertOperand(instruction, 0), ConvertOperand(instruction, 0), ConvertScalarFloatOperand(instruction, 1, true, context));
                else
                    goto default;

                break;

            case Mnemonic.Divss: // Divide Scalar Single Precision Floating-Point Values. DEST[31:0] = DEST[31:0] / SRC[31:0]
                Add(instruction.IP, ISIL.OpCode.Divide, ConvertOperand(instruction, 0), ConvertOperand(instruction, 0), ConvertScalarFloatOperand(instruction, 1, true, context));
                break;
            case Mnemonic.Vdivss: // VEX Divide Scalar Single Precision Floating-Point Values. DEST[31:0] = SRC1[31:0] / SRC2[31:0]
                Add(instruction.IP, ISIL.OpCode.Divide, ConvertOperand(instruction, 0), ConvertScalarFloatOperand(instruction, 1, true, context), ConvertScalarFloatOperand(instruction, 2, true, context));
                break;

            case Mnemonic.Ret:
                // TODO: Verify correctness of operation with Vectors.

                // On x32, this will require better engineering since ulongs are handled somehow differently (return in 2 registers, I think?)
                // The x64 prototype should work.
                // Are st* registers even used in il2cpp games?

                if (context.IsVoid)
                    Add(instruction.IP, ISIL.OpCode.Return);
                else if (context.Definition?.RawReturnType?.Type is Il2CppTypeEnum.IL2CPP_TYPE_R4 or Il2CppTypeEnum.IL2CPP_TYPE_R8)
                    Add(instruction.IP, ISIL.OpCode.Return, new ISIL.Register(null, "xmm0"));
                else
                    Add(instruction.IP, ISIL.OpCode.Return, new ISIL.Register(null, "rax"));
                break;
            case Mnemonic.Push:
                operandSize = instruction.Op0Kind == OpKind.Register ? instruction.Op0Register.GetSize() : instruction.MemorySize.GetSize();
                Add(instruction.IP, ISIL.OpCode.ShiftStack, Imm(-operandSize));
                Add(instruction.IP, ISIL.OpCode.Move, new ISIL.StackOffset(0), ConvertOperand(instruction, 0));
                break;
            case Mnemonic.Pop:
                operandSize = instruction.Op0Kind == OpKind.Register ? instruction.Op0Register.GetSize() : instruction.MemorySize.GetSize();
                Add(instruction.IP, ISIL.OpCode.Move, ConvertOperand(instruction, 0), new ISIL.StackOffset(0));
                Add(instruction.IP, ISIL.OpCode.ShiftStack, Imm(operandSize));
                break;
            case Mnemonic.Sub:
            case Mnemonic.Add:
                var isSubtract = instruction.Mnemonic == Mnemonic.Sub;

                // Special case - stack shift
                if (instruction.Op0Register == Register.RSP && instruction.Op1Kind.IsImmediate())
                {
                    var amount = (int)instruction.GetImmediate(1);
                    Add(instruction.IP, ISIL.OpCode.ShiftStack, Imm(isSubtract ? -amount : amount));
                    break;
                }

                var left = ConvertOperand(instruction, 0);
                var right = ConvertOperand(instruction, 1);
                if (isSubtract)
                    Add(instruction.IP, ISIL.OpCode.Subtract, left, left, right);
                else
                    Add(instruction.IP, ISIL.OpCode.Add, left, left, right);

                break;
            case Mnemonic.Addss:
            case Mnemonic.Subss:
                {
                    // Addss and subss are just floating point add/sub, but we don't need to handle the stack stuff
                    // But we do need to handle 2 vs 3 operand forms
                    ISIL.IOperand dest;
                    ISIL.IOperand src1;
                    ISIL.IOperand src2;

                    if (instruction.OpCount == 3)
                    {
                        //dest, src1, src2
                        dest = ConvertOperand(instruction, 0);
                        src1 = ConvertScalarFloatOperand(instruction, 1, true, context);
                        src2 = ConvertScalarFloatOperand(instruction, 2, true, context);
                    }
                    else if (instruction.OpCount == 2)
                    {
                        //DestAndSrc1, Src2
                        dest = ConvertOperand(instruction, 0);
                        src1 = dest;
                        src2 = ConvertScalarFloatOperand(instruction, 1, true, context);
                    }
                    else
                        goto default;

                    if (instruction.Mnemonic == Mnemonic.Subss)
                        Add(instruction.IP, ISIL.OpCode.Subtract, dest, src1, src2);
                    else
                        Add(instruction.IP, ISIL.OpCode.Add, dest, src1, src2);
                    break;
                }
            // The following pair of instructions does not update the Carry Flag (CF):
            case Mnemonic.Dec:
                Add(instruction.IP, ISIL.OpCode.Subtract, ConvertOperand(instruction, 0), ConvertOperand(instruction, 0), Imm(1));
                break;
            case Mnemonic.Inc:
                Add(instruction.IP, ISIL.OpCode.Add, ConvertOperand(instruction, 0), ConvertOperand(instruction, 0), Imm(1));
                break;

            case Mnemonic.Call:
                if (instruction.CodeSize == CodeSize.Code64 &&
                    (instruction.HasLockPrefix || instruction.HasRepPrefix || instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None))
                {
                    Add(instruction.IP, ISIL.OpCode.NotImplemented,
                        new ISIL.StringLiteral("Native call prefixes require independent semantics: " + FormatInstruction(instruction)));
                    break;
                }
                if (instruction.Code == Code.Call_rel32_64 &&
                    X86RuntimeNullThrowProof.TryIdentify(context.AppContext, instruction.NearBranchTarget) is { } nullThrow)
                {
                    // Keep the target runtime operation opaque until a proved receiver call
                    // preserves its implicit check. It has no standalone newobj/throw emission.
                    Add(instruction.IP, ISIL.OpCode.RuntimeNullThrow, nullThrow);
                    break;
                }
                var target = instruction.NearBranchTarget;

                if (instruction.Op0Kind == OpKind.Register || instruction.Op0Kind == OpKind.Memory)
                {
                    AddIndirectCall(instruction);
                }
                else if (context.AppContext.MethodsByAddress.TryGetValue(target, out var possibleMethods))
                {
                    if (possibleMethods.Count == 1)
                    {
                        ISIL.Instruction call;

                        if (possibleMethods[0].IsVoid)
                            call = Add(instruction.IP, ISIL.OpCode.CallVoid, Imm(target));
                        else
                            call = Add(instruction.IP, ISIL.OpCode.Call, Imm(target), CallingConventions.ReturnRegister(possibleMethods[0]));

                        call.AddOperands(CallingConventions.ResolveForManaged(possibleMethods[0]));
                        call.ImplicitDefinition = ReturnRegisterClobberedBy(possibleMethods[0]);
                    }
                    else
                    {
                        MethodAnalysisContext ctx = null!;
                        var lpars = -1;

                        // Very naive approach, folds with structs in parameters if GCC is used:
                        foreach (var method in possibleMethods)
                        {
                            var pars = method.Parameters.Count;
                            if (method.IsStatic) pars++;
                            if (pars > lpars)
                            {
                                lpars = pars;
                                ctx = method;
                            }
                        }

                        // On post-analysis, you can discard methods according to the registers used, see CallingConventions.
                        // This is less effective on GCC because MSVC doesn't overlap registers.

                        ISIL.Instruction call;

                        if (ctx.IsVoid)
                            call = Add(instruction.IP, ISIL.OpCode.CallVoid, Imm(target));
                        else
                            call = Add(instruction.IP, ISIL.OpCode.Call, Imm(target), CallingConventions.ReturnRegister(ctx));

                        call.AddOperands(CallingConventions.ResolveForManaged(ctx));
                        call.ImplicitDefinition = ReturnRegisterClobberedBy(ctx);
                    }
                }
                else
                {
                    // This isn't a managed method, so for now we don't know its parameter count.
                    // This will need to be rewritten if we ever stumble upon an unmanaged method that accepts more than 4 parameters.
                    // These can be converted to dedicated ISIL instructions for specific API functions at a later stage. (by a post-processing step)

                    var call = Add(instruction.IP, ISIL.OpCode.Call, Imm(target), new ISIL.Register(null, "rax") /* return value */);
                    call.AddOperands(CallingConventions.ResolveForUnmanaged(context.AppContext, target));
                }

                if (callNoReturn)
                {
                    // Our function decided to jump into a thunk or do a funny return.
                    // We will insert a return after the call.
                    // According to common sense, such callee must have the same return value as the caller, unless it's __noreturn.
                    // I hope someone else will catch up on this and figure out non-returning functions.

                    // TODO: Determine whether a function is an actual thunk and it's *technically better* to duplicate code for it, or if it's a regular retcall.
                    // Basic implementation may use context.AppContext.MethodsByAddress, but this doesn't catch thunks only.
                    // For example, SWDT often calls gc::GarbageCollector::SetWriteBarrier through a long jmp chain. That's a whole function, not just a thunk.

                    goto case Mnemonic.Ret;
                }

                break;
            case Mnemonic.Test:
            case Mnemonic.Cmp:
                operandSize = (instruction.Op0Kind == OpKind.Register ? instruction.Op0Register.GetSize() : instruction.MemorySize.GetSize()) * 8;
                var narrowZero = operandSize == 8 && instruction.Op1Kind == OpKind.Immediate8 && instruction.Immediate8 == 0 ||
                                 operandSize == 16 && (instruction.Op1Kind == OpKind.Immediate8to16 && instruction.Immediate8to16 == 0 ||
                                                       instruction.Op1Kind == OpKind.Immediate16 && instruction.Immediate16 == 0);
                if (instruction.Mnemonic == Mnemonic.Cmp && narrowZero && instruction.Op0Kind == OpKind.Memory &&
                    instruction.MemoryBase.GetSize() == 8 && instruction.MemoryBase != Register.RIP && instruction.MemoryIndex == Register.None &&
                    instruction.SegmentPrefix == Register.None && !instruction.HasLockPrefix)
                {
                    // Zero/nonzero is invariant under signed or unsigned narrow extension. Keep
                    // the read width until metadata proves a matching instance field at emission.
                    // Other flag consumers receive unresolved assignments from the common clobber
                    // path; this does not admit partial registers or drop volatile barrier calls.
                    var capturedNarrow = CaptureComparisonOperand(instruction.IP, ConvertOperand(instruction, 0),
                        operandSize == 8 ? "COMPARE_BYTE" : "COMPARE_WORD", operandSize);
                    Add(instruction.IP, ISIL.OpCode.CheckEqual, new ISIL.Register(null, "ZF"), capturedNarrow, Imm(0)).IntegerBitWidth = operandSize;
                    break;
                }
                if (operandSize is not (32 or 64))
                {
                    var reason = unresolvedMetadataGuard
                        ? "Runtime metadata initialization guard has unproved managed effects: "
                        : "Narrow integer comparison requires partial-register semantics: ";
                    Add(instruction.IP, ISIL.OpCode.NotImplemented, new ISIL.StringLiteral(reason + FormatInstruction(instruction)));
                    break;
                }
                if (instruction.Mnemonic == Mnemonic.Cmp ||
                    instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register && instruction.Op0Register == instruction.Op1Register)
                    AddCompareInstruction(instruction.IP, ConvertOperand(instruction, 0), instruction.Mnemonic == Mnemonic.Test ? Imm(0) : ConvertOperand(instruction, 1), operandSize);
                else
                    AddTestInstruction(instruction.IP, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1), operandSize);
                break;
            case Mnemonic.Comiss:
            case Mnemonic.Comisd:
            case Mnemonic.Ucomiss:
            case Mnemonic.Ucomisd:
                if (instruction.Op0Kind != OpKind.Register || instruction.Op1Kind != OpKind.Register)
                {
                    Add(instruction.IP, ISIL.OpCode.NotImplemented,
                        new ISIL.StringLiteral("Floating comparison memory operands require an exact read-width proof"));
                    break;
                }
                // Scalar compare flags have four outcomes, not integer subtraction semantics.
                // These predicates model managed comparison results; floating status/control
                // register observations are not represented by the managed recovery pipeline.
                var floatWidth = instruction.Mnemonic is Mnemonic.Comiss or Mnemonic.Ucomiss ? 32 : 64;
                var floatLeft = ConvertOperand(instruction, 0);
                var floatRight = ConvertOperand(instruction, 1);
                Add(instruction.IP, ISIL.OpCode.FloatCompare, new ISIL.Register(null, "CF"), floatLeft, floatRight, Imm(floatWidth), Imm(9));
                Add(instruction.IP, ISIL.OpCode.FloatCompare, new ISIL.Register(null, "ZF"), floatLeft, floatRight, Imm(floatWidth), Imm(10));
                Add(instruction.IP, ISIL.OpCode.FloatCompare, new ISIL.Register(null, "PF"), floatLeft, floatRight, Imm(floatWidth), Imm(8));
                break;
            case Mnemonic.Maxss:
            case Mnemonic.Minss:
                // These operations have unordered/NaN and signed-zero behavior which integer flags do not model.
                Add(instruction.IP, ISIL.OpCode.NotImplemented, new ISIL.StringLiteral("Floating comparison semantics are not recovered: " + FormatInstruction(instruction)));
                break;

            case Mnemonic.Cmove:
            case Mnemonic.Cmovne:
            case Mnemonic.Cmova:
            case Mnemonic.Cmovg:
            case Mnemonic.Cmovae:
            case Mnemonic.Cmovge:
            case Mnemonic.Cmovb:
            case Mnemonic.Cmovl:
            case Mnemonic.Cmovbe:
            case Mnemonic.Cmovle:
            case Mnemonic.Cmovs:
            case Mnemonic.Cmovns:
                if (instruction.Op1Kind == OpKind.Memory)
                {
                    // The native memory read occurs even when the move condition is false.
                    Add(instruction.IP, ISIL.OpCode.NotImplemented, new ISIL.StringLiteral("Conditional move with an unconditional memory read is not recovered: " + FormatInstruction(instruction)));
                    break;
                }
                Add(instruction.IP, ISIL.OpCode.ConditionalJump, Imm(instruction.IP + 1), FlagCondition(instruction.IP, instruction.ConditionCode, invert: true));
                Add(instruction.IP, ISIL.OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1));
                Add(instruction.IP + 1, ISIL.OpCode.Nop);
                break;

            case Mnemonic.Sete:
            case Mnemonic.Setne:
            case Mnemonic.Seta:
            case Mnemonic.Setae:
            case Mnemonic.Setb:
            case Mnemonic.Setbe:
            case Mnemonic.Setg:
            case Mnemonic.Setge:
            case Mnemonic.Setl:
            case Mnemonic.Setle:
            case Mnemonic.Sets:
            case Mnemonic.Setns:
                Add(instruction.IP, ISIL.OpCode.Move, ConvertOperand(instruction, 0), FlagCondition(instruction.IP, instruction.ConditionCode));
                break;

            case Mnemonic.Cmpxchg: // compare and exchange
                {
                    var accumulator = new ISIL.Register(null, instruction.Op1Register.GetSize() switch
                    {
                        8 => X86Utils.GetRegisterName(Register.RAX),
                        4 => X86Utils.GetRegisterName(Register.EAX),
                        2 => X86Utils.GetRegisterName(Register.AX),
                        1 => X86Utils.GetRegisterName(Register.AL),
                        _ => throw new NotSupportedException("unexpected behavior")
                    });
                    var dest = ConvertOperand(instruction, 0);
                    var src = ConvertOperand(instruction, 1);
                    operandSize = instruction.Op1Register.GetSize() * 8;
                    if (operandSize is not (32 or 64))
                    {
                        Add(instruction.IP, ISIL.OpCode.NotImplemented, new ISIL.StringLiteral("Narrow compare-exchange is not recovered: " + FormatInstruction(instruction)));
                        break;
                    }
                    AddCompareInstruction(instruction.IP, accumulator, dest, operandSize); // compare dest & accumulator
                    Add(instruction.IP, ISIL.OpCode.Not, new ISIL.Register(null, "TEMP"), new ISIL.Register(null, "ZF")); // TEMP = !ZF
                    Add(instruction.IP, ISIL.OpCode.ConditionalJump, Imm(instruction.IP + 1), new ISIL.Register(null, "TEMP")); // if accumulator == dest
                                                                                                                           // SET ZF = 1
                    Add(instruction.IP, ISIL.OpCode.Move, dest, src); // DEST = SRC
                    Add(instruction.IP, ISIL.OpCode.Jump, Imm(instruction.IP + 2)); // END IF
                                                                               // ELSE
                                                                               // SET ZF = 0
                    Add(instruction.IP + 1, ISIL.OpCode.Move, accumulator, dest); // accumulator = dest

                    Add(instruction.IP + 2, ISIL.OpCode.Nop); // exit for IF
                    break;
                }

            case Mnemonic.Jmp:
                if (instruction.Op0Kind != OpKind.Register)
                {
                    var jumpTarget = instruction.NearBranchTarget;

                    var methodStart = context.UnderlyingPointer;

                    if (TargetsOutsideMethod(jumpTarget, methodStart, context.RawBytes.Length))
                    {
                        callNoReturn = true;
                        goto case Mnemonic.Call;
                    }
                    else
                    {
                        Add(instruction.IP, ISIL.OpCode.Jump, Imm(jumpTarget));
                        break;
                    }
                }
                if (instruction.Op0Kind == OpKind.Register) // ex: jmp rax
                {
                    AddIndirectJmp(instruction);
                    break;
                }

                goto default;
            case Mnemonic.Je:
            case Mnemonic.Jne:
            case Mnemonic.Js:
            case Mnemonic.Jns:
            case Mnemonic.Jg:
            case Mnemonic.Ja:
            case Mnemonic.Jl:
            case Mnemonic.Jb:
            case Mnemonic.Jge:
            case Mnemonic.Jae:
            case Mnemonic.Jle:
            case Mnemonic.Jbe:
            case Mnemonic.Jp:
            case Mnemonic.Jnp:
                Add(instruction.IP, ISIL.OpCode.ConditionalJump, Imm(instruction.NearBranchTarget), FlagCondition(instruction.IP, instruction.ConditionCode));
                break;
            case Mnemonic.Xchg:
                Add(instruction.IP, ISIL.OpCode.Move, new ISIL.Register(null, "TEMP"), ConvertOperand(instruction, 0)); // TEMP = op0
                Add(instruction.IP, ISIL.OpCode.Move, ConvertOperand(instruction, 0), ConvertOperand(instruction, 1)); // op0 = op1
                Add(instruction.IP, ISIL.OpCode.Move, ConvertOperand(instruction, 1), new ISIL.Register(null, "TEMP")); // op1 = TEMP
                break;
            case Mnemonic.Int:
            case Mnemonic.Int3:
                Add(instruction.IP, ISIL.OpCode.Interrupt); // We'll add it but eliminate later, can be used as a hint since compilers only emit it in normally unreachable code or in error handlers
                break;
            case Mnemonic.Prefetchw: // Fetches the cache line containing the specified byte from memory to the 1st or 2nd level cache, invalidating other cached copies.
            case Mnemonic.Nop:
                // While this is literally a nop and there's in theory no point emitting anything for it, it could be used as a jump target.
                // So we'll emit an ISIL nop for it.
                Add(instruction.IP, ISIL.OpCode.Nop);
                break;
            default:
                Add(instruction.IP, ISIL.OpCode.NotImplemented, new ISIL.StringLiteral(FormatInstruction(instruction)));
                break;
        }

        // Jcc, setcc, and cmovcc share the same architectural flag predicates. Keep their
        // boolean operations unannotated: a comparison's result is a 0/1 value, not a native-width integer.
        ISIL.IOperand FlagCondition(ulong ip, ConditionCode condition, bool invert = false)
        {
            var cf = new ISIL.Register(null, "CF");
            var zf = new ISIL.Register(null, "ZF");
            var sf = new ISIL.Register(null, "SF");
            var of = new ISIL.Register(null, "OF");
            var pf = new ISIL.Register(null, "PF");
            var temp = new ISIL.Register(null, "CONDITION");
            var temp2 = new ISIL.Register(null, "CONDITION2");
            ISIL.IOperand result;
            switch (condition)
            {
                case ConditionCode.e: result = zf; break;
                case ConditionCode.p: result = pf; break;
                case ConditionCode.np:
                    Add(ip, ISIL.OpCode.CheckEqual, temp, pf, Imm(0));
                    result = temp;
                    break;
                case ConditionCode.ne:
                    Add(ip, ISIL.OpCode.CheckEqual, temp, zf, Imm(0));
                    result = temp;
                    break;
                case ConditionCode.b: result = cf; break;
                case ConditionCode.ae:
                    Add(ip, ISIL.OpCode.CheckEqual, temp, cf, Imm(0));
                    result = temp;
                    break;
                case ConditionCode.a:
                    Add(ip, ISIL.OpCode.CheckEqual, temp, cf, Imm(0));
                    Add(ip, ISIL.OpCode.CheckEqual, temp2, zf, Imm(0));
                    Add(ip, ISIL.OpCode.And, temp, temp, temp2);
                    result = temp;
                    break;
                case ConditionCode.be:
                    Add(ip, ISIL.OpCode.Or, temp, cf, zf);
                    result = temp;
                    break;
                case ConditionCode.s: result = sf; break;
                case ConditionCode.ns:
                    Add(ip, ISIL.OpCode.CheckEqual, temp, sf, Imm(0));
                    result = temp;
                    break;
                case ConditionCode.ge:
                case ConditionCode.l:
                    Add(ip, condition == ConditionCode.ge ? ISIL.OpCode.CheckEqual : ISIL.OpCode.CheckNotEqual, temp, sf, of);
                    result = temp;
                    break;
                case ConditionCode.g:
                    Add(ip, ISIL.OpCode.CheckEqual, temp, sf, of);
                    Add(ip, ISIL.OpCode.CheckEqual, temp2, zf, Imm(0));
                    Add(ip, ISIL.OpCode.And, temp, temp, temp2);
                    result = temp;
                    break;
                case ConditionCode.le:
                    Add(ip, ISIL.OpCode.CheckNotEqual, temp, sf, of);
                    Add(ip, ISIL.OpCode.Or, temp, temp, zf);
                    result = temp;
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(condition));
            }
            if (!invert)
                return result;
            Add(ip, ISIL.OpCode.CheckEqual, temp, result, Imm(0));
            return temp;
        }

        void AddCompareInstruction(ulong ip, ISIL.IOperand op0, ISIL.IOperand op1, int width)
        {
            op0 = CaptureComparisonOperand(ip, op0, "COMPARE_LEFT");
            op1 = CaptureComparisonOperand(ip, op1, "COMPARE_RIGHT");
            var difference = new ISIL.Register(null, "TEMP1");
            var operandsXor = new ISIL.Register(null, "TEMP2");
            var resultXor = new ISIL.Register(null, "TEMP3");
            var overflowBits = new ISIL.Register(null, "TEMP4");

            Add(ip, ISIL.OpCode.CheckLessUnsigned, new ISIL.Register(null, "CF"), op0, op1).IntegerBitWidth = width;
            Add(ip, ISIL.OpCode.Subtract, difference, op0, op1).IntegerBitWidth = width;
            Add(ip, ISIL.OpCode.Xor, operandsXor, op0, op1).IntegerBitWidth = width;
            Add(ip, ISIL.OpCode.Xor, resultXor, op0, difference).IntegerBitWidth = width;
            Add(ip, ISIL.OpCode.And, overflowBits, operandsXor, resultXor).IntegerBitWidth = width;
            Add(ip, ISIL.OpCode.CheckLess, new ISIL.Register(null, "OF"), overflowBits, Imm(0)).IntegerBitWidth = width;
            Add(ip, ISIL.OpCode.CheckLess, new ISIL.Register(null, "SF"), difference, Imm(0)).IntegerBitWidth = width;
            Add(ip, ISIL.OpCode.CheckEqual, new ISIL.Register(null, "ZF"), difference, Imm(0)).IntegerBitWidth = width;
            // Parity consumers remain unsupported; do not fabricate a parity value from the low bit.
        }

        void AddTestInstruction(ulong ip, ISIL.IOperand op0, ISIL.IOperand op1, int width)
        {
            op0 = CaptureComparisonOperand(ip, op0, "COMPARE_LEFT");
            op1 = CaptureComparisonOperand(ip, op1, "COMPARE_RIGHT");
            var bits = new ISIL.Register(null, "TEMP");
            Add(ip, ISIL.OpCode.And, bits, op0, op1).IntegerBitWidth = width;
            Add(ip, ISIL.OpCode.CheckEqual, new ISIL.Register(null, "ZF"), bits, Imm(0)).IntegerBitWidth = width;
            Add(ip, ISIL.OpCode.CheckLess, new ISIL.Register(null, "SF"), bits, Imm(0)).IntegerBitWidth = width;
            Add(ip, ISIL.OpCode.Move, new ISIL.Register(null, "CF"), Imm(0));
            Add(ip, ISIL.OpCode.Move, new ISIL.Register(null, "OF"), Imm(0));
        }

        ISIL.IOperand CaptureComparisonOperand(ulong ip, ISIL.IOperand operand, string name, int width = 0)
        {
            if (operand is not ISIL.MemoryOperand)
                return operand;
            // Native CMP reads each operand once, even though several flags depend on it.
            var captured = new ISIL.Register(null, name);
            Add(ip, ISIL.OpCode.Move, captured, operand).IntegerBitWidth = width;
            return captured;
        }
    }

    private ISIL.IOperand ConvertScalarFloatOperand(Instruction instruction, int operand, bool single, MethodAnalysisContext? context)
    {
        if (context == null || instruction.GetOpKind(operand) != OpKind.Memory)
            return ConvertOperand(instruction, operand);

        if (!instruction.IsIPRelativeMemoryOperand && instruction is not { MemoryBase: Register.None, MemoryIndex: Register.None })
            return ConvertOperand(instruction, operand);

        var address = instruction.IsIPRelativeMemoryOperand ? instruction.IPRelativeMemoryAddress : instruction.MemoryDisplacement64;

        return ReadFloatConstant(context.AppContext.Binary, address, single) ?? ConvertOperand(instruction, operand);
    }

    private static ISIL.IOperand? ReadFloatConstant(LibCpp2IL.Il2CppBinary binary, ulong addr, bool single)
    {
        if (!binary.TryMapVirtualAddressToRaw(addr, out var raw))
            return null;

        var content = binary.GetRawBinaryContent();
        var size = single ? 4 : 8;

        if (raw < 0 || raw + size > content.Length)
            return null;

        var bytes = content.Slice((int)raw, size).ToArray();

        return single
            ? new ISIL.FloatLiteral(BitConverter.ToSingle(bytes, 0))
            : new ISIL.DoubleLiteral(BitConverter.ToDouble(bytes, 0));
    }

    private ISIL.IOperand ConvertOperand(Instruction instruction, int operand, bool isLeaAddress = false)
    {
        var kind = instruction.GetOpKind(operand);

        if (kind == OpKind.Register)
            return new ISIL.Register(null, X86Utils.GetRegisterName(instruction.GetOpRegister(operand)));
        if (kind.IsImmediate())
            return new ISIL.Immediate((long)instruction.GetImmediate(operand));
        if (kind == OpKind.Memory && instruction.MemoryBase == Register.RSP)
        {
            var slot = new ISIL.StackOffset((int)instruction.MemoryDisplacement32);
            return isLeaAddress ? new ISIL.AddressOf(slot) : slot;
        }

        //Memory
        //Most complex to least complex

        if (instruction.IsIPRelativeMemoryOperand)
            return new ISIL.MemoryOperand(addend: (long)instruction.IPRelativeMemoryAddress);

        //All four components
        if (instruction.MemoryIndex != Register.None && instruction.MemoryBase != Register.None && instruction.MemoryDisplacement64 != 0)
        {
            var mBase = new ISIL.Register(null, X86Utils.GetRegisterName(instruction.MemoryBase));
            var mIndex = new ISIL.Register(null, X86Utils.GetRegisterName(instruction.MemoryIndex));
            return new ISIL.MemoryOperand(mBase, mIndex, instruction.MemoryDisplacement32, instruction.MemoryIndexScale);
        }

        //No addend
        if (instruction.MemoryIndex != Register.None && instruction.MemoryBase != Register.None)
        {
            var mBase = new ISIL.Register(null, X86Utils.GetRegisterName(instruction.MemoryBase));
            var mIndex = new ISIL.Register(null, X86Utils.GetRegisterName(instruction.MemoryIndex));
            return new ISIL.MemoryOperand(mBase, mIndex, scale: instruction.MemoryIndexScale);
        }

        //No base
        if (instruction.MemoryIndex != Register.None && instruction.MemoryDisplacement64 != 0)
        {
            var mIndex = new ISIL.Register(null, X86Utils.GetRegisterName(instruction.MemoryIndex));
            return new ISIL.MemoryOperand(null, mIndex, instruction.MemoryDisplacement32, instruction.MemoryIndexScale);
        }

        //No index (and so no scale)
        if (instruction.MemoryBase != Register.None && instruction.MemoryDisplacement64 > 0)
        {
            var mBase = new ISIL.Register(null, X86Utils.GetRegisterName(instruction.MemoryBase));
            return new ISIL.MemoryOperand(mBase, addend: (long)instruction.MemoryDisplacement64);
        }

        //Only base
        if (instruction.MemoryBase != Register.None)
        {
            return new ISIL.MemoryOperand(new ISIL.Register(null, X86Utils.GetRegisterName(instruction.MemoryBase)));
        }

        //Only addend
        return new ISIL.MemoryOperand(addend: (long)instruction.MemoryDisplacement64);
    }
}
