using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.PE;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using Register = Cpp2IL.Core.ISIL.Register;
using NativeInstruction = Iced.Intel.Instruction;
using MemoryOperand = Cpp2IL.Core.ISIL.MemoryOperand;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Closed, separately rounded binary64 field sums and signed parameter scaling.</summary>
internal static partial class X64ScalarDoubleLeafProof
{
    internal const string EvidenceKey = "X64ScalarDoubleLeafProof";
    private const int MaximumBytes = 96;

    internal sealed class Evidence
    {
        private readonly NativeInstruction[] _body;
        private readonly MethodAnalysisContext[] _aliases;
        private readonly object[] _input;
        private readonly X64GenericMethodTableProof.Evidence _genericTables;
        internal MethodAnalysisContext Method { get; }
        internal Shape Native { get; }
        internal FieldAnalysisContext? FirstField { get; }
        internal FieldAnalysisContext? SecondField { get; }
        internal ulong? ScaleBits { get; }
        internal ReadOnlySpan<NativeInstruction> Body => _body;
        internal ReadOnlySpan<MethodAnalysisContext> Aliases => _aliases;

        internal Evidence(MethodAnalysisContext method, Shape native, NativeInstruction[] body,
            MethodAnalysisContext[] aliases, FieldAnalysisContext? first, FieldAnalysisContext? second,
            ulong? scale, object[] input, X64GenericMethodTableProof.Evidence genericTables)
        {
            Method = method;
            Native = native;
            _body = body.ToArray();
            _aliases = aliases.ToArray();
            FirstField = first;
            SecondField = second;
            ScaleBits = scale;
            _input = input.ToArray();
            _genericTables = genericTables;
        }

        internal bool IsUnchanged() => Method.AppContext.Binary is PE pe &&
            X64UnwindProof.ForApplication(Method.AppContext) is { } unwind &&
            _genericTables.Matches(Method.AppContext, pe, unwind) &&
            Find(Method) is { } current && Native == current.Native &&
            _body.SequenceEqual(current._body) && _aliases.SequenceEqual(current._aliases) &&
            ReferenceEquals(FirstField, current.FirstField) && ReferenceEquals(SecondField, current.SecondField) &&
            ScaleBits == current.ScaleBits && _input.SequenceEqual(current._input);
    }

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey) || method.GetExtraData<Evidence>(EvidenceKey) != null;

    internal static List<Instruction>? TryLift(MethodAnalysisContext method, IReadOnlyList<NativeInstruction> decoded)
    {
        if (!decoded.Take(5).Any(site => site.Code is Code.Addsd_xmm_xmmm64 or Code.Cvtsi2sd_xmm_rm64) ||
            Find(method) is not { } proof || !decoded.Take(proof.Body.Length).SequenceEqual(proof.Body.ToArray()))
            return null;
        if (HasEvidence(method) && (method.GetExtraData<Evidence>(EvidenceKey) is not { } saved || !saved.IsUnchanged()))
            return null;
        method.PutExtraData(EvidenceKey, proof);
        NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        var result = new List<Instruction>();
        var first = Temporary("first");
        var returned = Temporary("result");
        if (proof.Native.Operation == Kind.FieldSum)
        {
            var second = Temporary("second");
            Add(OpCode.Move, proof.Native.First.IP, first, Memory(proof.FirstField!));
            Add(OpCode.Move, proof.Native.Arithmetic.IP, second, Memory(proof.SecondField!));
            Add(OpCode.FloatAdd, proof.Native.Arithmetic.IP, returned, first, second, new Immediate(64));
        }
        else
        {
            // CVTSI2SD overwrites the demanded low64. A preceding self-XOR only
            // clears unobserved upper bits; it is not a recovered zero result.
            Add(OpCode.Int64ToDouble, proof.Native.First.IP, first, new Register(null, "rcx"));
            var scale = Temporary("scale");
            Add(OpCode.Move, proof.Native.Arithmetic.IP, scale,
                new DoubleLiteral(BitConverter.ToDouble(BitConverter.GetBytes(proof.ScaleBits!.Value), 0)));
            Add(OpCode.FloatMultiply, proof.Native.Arithmetic.IP, returned, first, scale, new Immediate(64));
        }
        Add(OpCode.Return, proof.Native.Return.IP, returned);
        return result;

        static Register Temporary(string suffix) => new(null, "double_leaf_" + suffix);
        static MemoryOperand Memory(FieldAnalysisContext field) => new(new Register(null, "rcx"), null, field.Offset);
        void Add(OpCode code, ulong address, params IOperand[] operands) =>
            result.Add(new(result.Count, code, operands.ToList()) { NativeAddress = address });
    }

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!ReferenceEquals(method.ReturnType, app.SystemTypes.SystemDoubleType) || app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                ReadCompleteBody(method, pe, unwind, out var closedBytes) is not { } body ||
                TryProveShape(body) is not { } shape ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null ||
                X64GenericMethodTableProof.TryIdentify(app, pe, unwind) is not { } genericTables ||
                !CompleteAliases(method, genericTables, out var aliases)) return null;
            var input = new List<object> { app, app.LibCpp2IlContext, app.Metadata, pe, closedBytes };
            FieldAnalysisContext? first = null, second = null;
            ulong? scale = null;
            foreach (var alias in aliases)
            {
                if (!Bind(alias, shape, pe, unwind, input, out var aliasFirst, out var aliasSecond, out var aliasScale))
                    return null;
                if (!ReferenceEquals(alias, method)) continue;
                first = aliasFirst; second = aliasSecond; scale = aliasScale;
            }
            return new(method, shape, body, aliases, first, second, scale, input.ToArray(), genericTables);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException or
                                          KeyNotFoundException or NullReferenceException)
        {
            return null;
        }
    }

    private static NativeInstruction[]? ReadCompleteBody(MethodAnalysisContext method, PE pe,
        X64UnwindProof.Index unwind, out string closedBytes)
    {
        closedBytes = "";
        if (method.RawBytes.Length == 0) method.EnsureRawBytes();
        var body = X64NativeInstructionReader.ReadRootBody(method);
        ulong end;
        if (body != null)
        {
            end = body[^1].NextIP;
            var count = body.Length;
            while (count > 0 && body[count - 1].Code == Code.Int3) count--;
            body = body.Take(count).ToArray();
            // A root span may be only one process-unwind fragment. Requiring
            // this complete straight-line recipe through RET closes that gap.
            if (TryProveShape(body) == null || end - method.UnderlyingPointer > MaximumBytes) return null;
        }
        else
        {
            var prefix = X86Utils.Iterate(method).Take(6).ToArray();
            var returned = Array.FindIndex(prefix, site => site.Code == Code.Retnq);
            if (returned < 0 || X64NativeInstructionReader.ReadFramelessLeaf(method, returned + 1, MaximumBytes) is not { } leaf ||
                TryProveShape(leaf) == null || leaf[^1].NextIP > ulong.MaxValue - 15) return null;
            body = leaf;
            end = (body[^1].NextIP + 15) & ~15UL;
            if (unwind.ClassifySpan(method.UnderlyingPointer, end).Kind != X64UnwindProof.SpanKind.NoEntry ||
                X64NativeInstructionReader.HasInteriorManagedEntry(method.AppContext, method.UnderlyingPointer, end) ||
                !X64NativePaddingProof.HasInt3Padding(pe, body[^1].NextIP, end) ||
                !unwind.IsUnaffectedByBaseRelocation(method.UnderlyingPointer, checked((uint)(end - method.UnderlyingPointer))))
                return null;
        }
        var offset = checked((int)pe.MapVirtualAddressToRaw(method.UnderlyingPointer, false));
        var bytes = pe.GetRawBinaryContent().Slice(offset, checked((int)(end - method.UnderlyingPointer)));
        if (!X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind, bytes, method.UnderlyingPointer)) return null;
        closedBytes = Convert.ToBase64String(bytes.ToArray());
        return body;
    }

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        try
        {
            if (method.GetExtraData<Evidence>(EvidenceKey) is not { } proof || !proof.IsUnchanged() ||
                !NativeStraightLineGraph.TryGetBody(method, out var instructions)) return false;
            var index = 0;
            var outputs = new HashSet<LocalVariable>();
            LocalVariable first;
            IOperand second;
            if (proof.Native.Operation == Kind.FieldSum)
            {
                if (!Read(proof.Native.First, proof.FirstField!, out first) ||
                    !Read(proof.Native.Arithmetic, proof.SecondField!, out var captured)) return false;
                second = captured;
            }
            else
            {
                if (!Next(OpCode.Int64ToDouble, proof.Native.First.IP, out var conversion) ||
                    !IntegerFloatConversion.HasCanonicalTypes(conversion, method.AppContext.SystemTypes) ||
                    conversion.Operands is not [LocalVariable converted, LocalVariable parameter] ||
                    !method.ParameterLocals.Contains(parameter) || parameter.IsThis || parameter.IsMethodInfo ||
                    LocalVariables.GetIncomingParameterIndex(method, parameter) != 0 ||
                    !ReferenceEquals(parameter.Type, method.Parameters[0].ParameterType) || !Output(converted)) return false;
                first = converted;
                if (index < instructions.Count && instructions[index].OpCode == OpCode.Move)
                {
                    if (!Next(OpCode.Move, proof.Native.Arithmetic.IP, out var coefficient) || coefficient.Operands is not
                            [LocalVariable scale, DoubleLiteral literal] || !MatchesScale(literal) || !Output(scale)) return false;
                    second = scale;
                }
                else
                {
                    if (index >= instructions.Count || instructions[index].Operands is not
                            [_, _, DoubleLiteral literal, _] || !MatchesScale(literal)) return false;
                    second = instructions[index].Operands[2];
                }
            }
            var code = proof.Native.Operation == Kind.FieldSum ? OpCode.FloatAdd : OpCode.FloatMultiply;
            if (!Next(code, proof.Native.Arithmetic.IP, out var arithmetic) ||
                arithmetic.Operands is not [LocalVariable result, LocalVariable left, var right, Immediate { Value: 64 }] ||
                !ReferenceEquals(left, first) || !ReferenceEquals(right, second) || !Output(result) ||
                (code == OpCode.FloatAdd ? !FloatAddSubtract.TryGet(arithmetic, out _) : !FloatMultiplication.TryGet(arithmetic, out _)) ||
                !Next(OpCode.Return, proof.Native.Return.IP, out var returned) || index != instructions.Count ||
                returned.Operands is not [LocalVariable value] || !ReferenceEquals(value, result)) return false;
            return true;

            bool Next(OpCode opcode, ulong address, out Instruction instruction)
            {
                instruction = null!;
                if (index >= instructions.Count) return false;
                instruction = instructions[index++];
                return instruction.OpCode == opcode && instruction.NativeAddress == address &&
                    instruction.IntegerBitWidth == 0 && instruction.CallSemantics == CallSemantics.Direct;
            }
            bool Output(LocalVariable local) => ReferenceEquals(local.Type, method.AppContext.SystemTypes.SystemDoubleType) &&
                !method.ParameterLocals.Contains(local) && outputs.Add(local);
            bool Read(NativeInstruction native, FieldAnalysisContext field, out LocalVariable captured)
            {
                captured = null!;
                if (!Next(OpCode.Move, native.IP, out var read) || read.Operands is not
                        [LocalVariable output, FieldReference access] || !ReferenceEquals(access.Field, field) ||
                    access.Offset != field.Offset || method.ParameterLocals.Where(local => local.IsThis).ToArray() is not [var receiver] ||
                    !ReferenceEquals(access.Local, receiver) || receiver.IsMethodInfo ||
                    !ReferenceEquals(receiver.Type, method.DeclaringType) ||
                    method.ParameterOperands is not [Register { Name: "rcx" } incoming, _] ||
                    incoming.Number != receiver.Register.Number || !Output(output)) return false;
                captured = output;
                return true;
            }
            bool MatchesScale(DoubleLiteral literal) =>
                BitConverter.ToUInt64(BitConverter.GetBytes(literal.Value), 0) == proof.ScaleBits;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException or
                                          KeyNotFoundException or NullReferenceException)
        {
            return false;
        }
    }
}
