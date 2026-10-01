using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core;

public static partial class IlGenerator
{
    private readonly record struct LaneValue(int Width, int Identity);
    private readonly record struct LaneKey(int Width, int Kind, ulong Value, int Left = 0, int Right = 0);
    private readonly record struct LaneOperation(ulong Address, OpCode Code, int Width,
        int Left, int Right, int Result);

    internal static void ValidateScalarLaneCopies(MethodAnalysisContext method)
    {
        if (!X64ScalarLaneDemandProof.HasEvidence(method)) return;
        if (method.GetExtraData<X64ScalarLaneDemandProof.Evidence>(X64ScalarLaneDemandProof.EvidenceKey) is not { } proof ||
            !proof.IsUnchanged() || !NativeStraightLineGraph.TryGetBody(method, out var instructions))
            throw new DecompilerException("Scalar lane copies lost their complete native body, original ABI, literal bits or typed ordered data flow");
        // The transport proof does not qualify an unsupported scalar operation.
        // GenerateIl's retained-lifting check reports that original diagnostic.
        if (instructions.Any(instruction => instruction.OpCode == OpCode.NotImplemented)) return;
        if (!ScalarLaneProgramMatches(method, proof, instructions))
            throw new DecompilerException("Scalar lane copies lost their complete native body, original ABI, literal bits or typed ordered data flow");
    }

    // Compare value identities rather than temporary names: normal SSA copy
    // propagation can remove every packed-copy site. Arithmetic remains in its
    // original order, with the same inputs and scalar width at each native site.
    internal static bool ScalarLaneProgramMatches(MethodAnalysisContext method,
        X64ScalarLaneDemandProof.Evidence proof, IReadOnlyList<Instruction> instructions)
    {
        var identities = new Dictionary<LaneKey, int>();
        var nativeValues = new Dictionary<NativeRegister, LaneValue>();
        var expected = new List<LaneOperation>();
        var locals = new Dictionary<LocalVariable, LaneValue>();
        var projections = proof.Shape.Projections.ToArray().ToDictionary(site => site.Address);
        foreach (var incoming in proof.Incoming)
            nativeValues.Add(incoming.Key, Parameter(incoming.Value, (int)incoming.Key - (int)NativeRegister.XMM0));

        foreach (var native in proof.Body)
        {
            if (native.Mnemonic is Mnemonic.Ret or Mnemonic.Nop) continue;
            if (projections.TryGetValue(native.IP, out var projection))
            {
                if (projection.IsZero)
                    nativeValues[projection.Destination] = Literal(projection.Width, 0);
                else if (!nativeValues.TryGetValue(projection.Source, out var source) || source.Width != projection.Width)
                    return false;
                else nativeValues[projection.Destination] = source;
                continue;
            }
            var width = native.MemorySize.GetSize() == 4 || native.Mnemonic is
                Mnemonic.Movss or Mnemonic.Addss or Mnemonic.Subss or Mnemonic.Mulss or Mnemonic.Divss ? 32 : 64;
            LaneValue right;
            if (native.Op1Kind == OpKind.Register)
            {
                if (!nativeValues.TryGetValue(native.Op1Register, out right) || right.Width != width) return false;
            }
            else
            {
                var read = proof.Literals.ToArray().FirstOrDefault(literal =>
                    literal.Address == native.IPRelativeMemoryAddress && literal.Width == width);
                if (read.Bytes == null) return false;
                var bytes = Convert.FromBase64String(read.Bytes);
                right = Literal(width, width == 32 ? BitConverter.ToUInt32(bytes, 0) : BitConverter.ToUInt64(bytes, 0));
            }
            if (native.Mnemonic is Mnemonic.Movss or Mnemonic.Movsd)
            {
                nativeValues[native.Op0Register] = right;
                continue;
            }
            var code = NativeArithmetic(native.Mnemonic);
            if (code == OpCode.Invalid || !nativeValues.TryGetValue(native.Op0Register, out var left) || left.Width != width)
                return false;
            var result = Arithmetic(width, code, native.IP, left, right);
            expected.Add(new(native.IP, code, width, left.Identity, right.Identity, result.Identity));
            nativeValues[native.Op0Register] = result;
        }
        if (!nativeValues.TryGetValue(NativeRegister.XMM0, out var returned) || returned.Width != proof.ReturnWidth ||
            instructions.Count == 0 || instructions[^1].OpCode != OpCode.Return) return false;

        var operation = 0;
        for (var ordinal = 0; ordinal < instructions.Count; ordinal++)
        {
            var instruction = instructions[ordinal];
            if (instruction.IntegerBitWidth != 0 || instruction.CallSemantics != CallSemantics.Direct) return false;
            if (instruction.OpCode == OpCode.Nop)
            {
                if (instruction.Operands.Count != 0) return false;
                continue;
            }
            if (instruction.OpCode == OpCode.Return)
                return ordinal == instructions.Count - 1 && operation == expected.Count &&
                    instruction.NativeAddress == proof.Body[^1].IP && instruction.Operands is [var value] &&
                    Read(value, out var actual) && actual == returned;
            if (instruction.OpCode == OpCode.Move)
            {
                if (instruction.Operands is not [LocalVariable destination, var source] ||
                    !Read(source, out var copied) || Width(destination) != copied.Width) return false;
                locals[destination] = copied;
                continue;
            }
            if (operation >= expected.Count || instruction.Operands is not
                    [LocalVariable output, var first, var second] || !Read(first, out var a) || !Read(second, out var b))
                return false;
            var step = expected[operation++];
            if (instruction.OpCode != step.Code || instruction.NativeAddress != step.Address ||
                Width(output) != step.Width || a.Width != step.Width || b.Width != step.Width ||
                a.Identity != step.Left || b.Identity != step.Right) return false;
            locals[output] = new(step.Width, step.Result);
        }
        return false;

        LaneValue Intern(LaneKey key)
        {
            if (!identities.TryGetValue(key, out var identity))
            {
                identity = identities.Count + 1;
                identities.Add(key, identity);
            }
            return new(key.Width, identity);
        }
        LaneValue Parameter(int width, int index) => Intern(new(width, 1, checked((ulong)index)));
        LaneValue Literal(int width, ulong bits) => Intern(new(width, 2, bits));
        LaneValue Arithmetic(int width, OpCode code, ulong address, LaneValue left, LaneValue right) =>
            Intern(new(width, 3 + (int)code, address, left.Identity, right.Identity));
        int Width(LocalVariable local) => ReferenceEquals(local.Type, method.AppContext.SystemTypes.SystemSingleType) ? 32 :
            ReferenceEquals(local.Type, method.AppContext.SystemTypes.SystemDoubleType) ? 64 : 0;
        bool Read(IOperand operand, out LaneValue value)
        {
            value = default;
            if (operand is FloatLiteral single)
            {
                value = Literal(32, BitConverter.ToUInt32(BitConverter.GetBytes(single.Value), 0));
                return true;
            }
            if (operand is DoubleLiteral twice)
            {
                value = Literal(64, BitConverter.ToUInt64(BitConverter.GetBytes(twice.Value), 0));
                return true;
            }
            if (operand is not LocalVariable local || Width(local) is not (32 or 64)) return false;
            if (locals.TryGetValue(local, out value)) return value.Width == Width(local);
            if (!method.ParameterLocals.Contains(local) ||
                LocalVariables.GetIncomingParameterIndex(method, local) is not { } index ||
                !ReferenceEquals(local.Type, method.Parameters[index].ParameterType)) return false;
            value = Parameter(Width(local), index);
            return true;
        }
    }

    private static OpCode NativeArithmetic(Mnemonic mnemonic) => mnemonic switch
    {
        Mnemonic.Addss or Mnemonic.Addsd => OpCode.Add,
        Mnemonic.Subss or Mnemonic.Subsd => OpCode.Subtract,
        Mnemonic.Mulss or Mnemonic.Mulsd => OpCode.Multiply,
        Mnemonic.Divss or Mnemonic.Divsd => OpCode.Divide,
        _ => OpCode.Invalid
    };
}
