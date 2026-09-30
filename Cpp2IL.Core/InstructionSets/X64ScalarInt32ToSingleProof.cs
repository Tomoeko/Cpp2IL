using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;
using Instruction = Cpp2IL.Core.ISIL.Instruction;
using Register = Cpp2IL.Core.ISIL.Register;
using NativeInstruction = Iced.Intel.Instruction;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>Authenticates closed signed Int32-to-binary32 stores, scaled reads and ratios.</summary>
internal static class X64ScalarInt32ToSingleProof
{
    internal const string EvidenceKey = "X64ScalarInt32ToSingleProof";
    internal enum Kind { Parameter, ScaledField, FieldRatio }
    internal sealed record Shape(Kind Operation, NativeInstruction FirstRead, NativeInstruction FirstConversion,
        NativeInstruction? SecondRead, NativeInstruction? SecondConversion, NativeInstruction? Decrement,
        NativeInstruction? Arithmetic, NativeInstruction? Store, NativeInstruction Return);
    internal sealed record Proof(Shape Native, NativeInstruction[] Body, int Parameter,
        FieldAnalysisContext? First, FieldAnalysisContext? Second, uint? ScaleBits, object[] Input)
    {
        internal bool Matches(Proof other) => Native == other.Native && Body.SequenceEqual(other.Body) &&
            Parameter == other.Parameter && ReferenceEquals(First, other.First) &&
            ReferenceEquals(Second, other.Second) && ScaleBits == other.ScaleBits && Input.SequenceEqual(other.Input);
    }

    internal static bool HasEvidence(MethodAnalysisContext method) =>
        NativeRecoveryProofTracker.Has(method, EvidenceKey) || method.GetExtraData<Proof>(EvidenceKey) != null;

    internal static List<Instruction>? TryLift(MethodAnalysisContext method, IReadOnlyList<NativeInstruction> decoded)
    {
        if (!decoded.Take(8).Any(native => native.Code == Code.Cvtdq2ps_xmm_xmmm128) ||
            Find(method) is not { } proof || !decoded.Take(proof.Body.Length).SequenceEqual(proof.Body)) return null;
        method.PutExtraData(EvidenceKey, proof);
        NativeRecoveryProofTracker.Mark(method, EvidenceKey);
        var result = new List<Instruction>();
        Register first;
        if (proof.Native.Operation == Kind.Parameter)
            first = new(null, X86Utils.GetRegisterName(proof.Native.FirstRead.Op1Register));
        else
        {
            first = Temporary("first_integer");
            Add(OpCode.Move, proof.Native.FirstRead.IP, first, Memory(proof.First!));
        }
        Register? second = null;
        if (proof.Native.SecondRead is { } secondRead)
        {
            second = Temporary("second_integer");
            Add(OpCode.Move, secondRead.IP, second, Memory(proof.Second!));
        }
        if (proof.Native.Decrement is { } decrement)
        {
            var reduced = Temporary("reduced_integer");
            result.Add(new(result.Count, OpCode.Subtract, reduced, first, new Immediate(1))
                { NativeAddress = decrement.IP, IntegerBitWidth = 32 });
            first = reduced;
        }
        var converted = Temporary("first_single");
        Add(OpCode.Int32ToSingle, proof.Native.FirstConversion.IP, converted, first);
        var returned = converted;
        if (second != null)
        {
            var divisor = Temporary("second_single");
            Add(OpCode.Int32ToSingle, proof.Native.SecondConversion!.Value.IP, divisor, second);
            returned = Temporary("ratio");
            Add(OpCode.FloatDivide, proof.Native.Arithmetic!.Value.IP, returned, converted, divisor, new Immediate(32));
        }
        else if (proof.ScaleBits is { } bits)
        {
            var scale = Temporary("scale");
            Add(OpCode.Move, proof.Native.Arithmetic!.Value.IP, scale,
                new FloatLiteral(BitConverter.ToSingle(BitConverter.GetBytes(bits), 0)));
            returned = Temporary("scaled_single");
            Add(OpCode.FloatMultiply, proof.Native.Arithmetic.Value.IP, returned, converted, scale, new Immediate(32));
        }
        if (proof.Native.Store is { } store)
            Add(OpCode.Move, store.IP, Memory(proof.First!), returned);
        Add(OpCode.Return, proof.Native.Return.IP, proof.Native.Store == null ? [returned] : []);
        return result;

        static Register Temporary(string suffix) => new(null, "int32_single_" + suffix);
        static ISIL.MemoryOperand Memory(FieldAnalysisContext field) => new(new Register(null, "rcx"), null, field.Offset);
        void Add(OpCode code, ulong address, params IOperand[] operands) =>
            result.Add(new(result.Count, code, operands.ToList()) { NativeAddress = address });
    }

    internal static Proof? Find(MethodAnalysisContext method)
    {
        try
        {
            if (!Signature(method) || method.AppContext.Binary is not PE pe ||
                X64UnwindProof.ForApplication(method.AppContext) is not { } unwind) return null;
            if (method.RawBytes.Length == 0) method.EnsureRawBytes();
            var prefix = X86Utils.Iterate(method).Take(8).ToArray();
            var terminal = Array.FindIndex(prefix, instruction => instruction.Code == Code.Retnq);
            if (terminal < 0 || TryProveShape(prefix.Take(terminal + 1).ToArray()) is not { } shape ||
                X64NativeInstructionReader.ReadFramelessBody(method, terminal + 1, 64) is not { } body ||
                TryProveShape(body) != shape || X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null)
                return null;
            var system = method.AppContext.SystemTypes;
            FieldAnalysisContext? first = null, second = null;
            var parameter = -1;
            uint? bits = null;
            if (shape.Operation == Kind.Parameter)
            {
                if (method.Parameters.Count != 1 || ParameterFor(method, shape.FirstRead.Op1Register) != 0)
                    return null;
                parameter = 0;
                if (shape.Store is { } store)
                {
                    first = Field(method, store, system.SystemSingleType, true);
                    if (first == null || method.IsStatic || !method.IsVoid) return null;
                }
                else if (!ReferenceEquals(method.ReturnType, system.SystemSingleType)) return null;
            }
            else
            {
                if (method.IsStatic || method.Parameters.Count != 0 ||
                    !ReferenceEquals(method.ReturnType, system.SystemSingleType)) return null;
                first = Field(method, shape.FirstRead, null, false);
                if (first == null) return null;
                if (shape.Operation == Kind.FieldRatio)
                {
                    second = Field(method, shape.SecondRead!.Value, system.SystemInt32Type, false);
                    if (second == null || !ReferenceEquals(first.FieldType, system.SystemInt32Type)) return null;
                }
                else
                {
                    var address = shape.Arithmetic!.Value.IPRelativeMemoryAddress;
                    var offset = unwind.MapReadOnlyData(address, 4);
                    if (offset < 0 || !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, address, 4)) return null;
                    var data = pe.GetRawBinaryContent().Slice(offset, 4);
                    bits = (uint)(data[0] | data[1] << 8 | data[2] << 16 | data[3] << 24);
                    // This bounded scaling cohort consumes a finite binary32 constant.
                    if ((bits.Value & 0x7F800000u) == 0x7F800000u) return null;
                }
            }
            var input = Snapshot(method, first, second);
            return input == null ? null : new(shape, body, parameter, first, second, bits, input);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    internal static bool IsValidFor(MethodAnalysisContext method)
    {
        try
        {
            if (!NativeRecoveryProofTracker.Has(method, EvidenceKey) ||
                method.GetExtraData<Proof>(EvidenceKey) is not { } original || Find(method) is not { } current ||
                !original.Matches(current) || !NativeStraightLineGraph.TryGetBody(method, out var instructions)) return false;
            var index = 0;
            var outputs = new HashSet<LocalVariable>();
            var expected = original.Native;
            LocalVariable first;
            if (expected.Operation == Kind.Parameter)
            {
                if (instructions.Count == 0 || instructions[0].Operands is not [_, LocalVariable incoming] ||
                    !method.ParameterLocals.Contains(incoming) ||
                    LocalVariables.GetIncomingParameterIndex(method, incoming) != original.Parameter ||
                    !ReferenceEquals(incoming.Type, method.AppContext.SystemTypes.SystemInt32Type)) return false;
                first = incoming;
            }
            else if (!Read(expected.FirstRead, original.First!, out first)) return false;
            LocalVariable? second = null;
            if (expected.SecondRead is { } secondRead && !Read(secondRead, original.Second!, out second)) return false;
            if (expected.Decrement is { } decrement)
            {
                if (!Next(OpCode.Subtract, decrement.IP, out var subtraction, 32) || subtraction.Operands is not
                        [LocalVariable reduced, LocalVariable input, Immediate { Value: 1 }] ||
                    !ReferenceEquals(input, first) || !Output(reduced, method.AppContext.SystemTypes.SystemInt32Type)) return false;
                first = reduced;
            }
            if (!Convert(expected.FirstConversion, first, out var converted)) return false;
            var result = converted;
            if (second != null)
            {
                if (!Convert(expected.SecondConversion!.Value, second, out var divisor) ||
                    !Next(OpCode.FloatDivide, expected.Arithmetic!.Value.IP, out var division) ||
                    !FloatDivision.TryGet(division, out var precision) || precision.Width != 32 ||
                    !Binary(division, converted, divisor, out result)) return false;
            }
            else if (original.ScaleBits is { } bits)
            {
                IOperand coefficient;
                if (index < instructions.Count && instructions[index].OpCode == OpCode.Move)
                {
                    if (!Next(OpCode.Move, expected.Arithmetic!.Value.IP, out var constant) || constant.Operands is not
                            [LocalVariable scale, FloatLiteral literal] || !MatchesScale(literal, bits) ||
                        !Output(scale, method.AppContext.SystemTypes.SystemSingleType)) return false;
                    coefficient = scale;
                }
                else
                {
                    // Pure constant propagation may eliminate the temporary;
                    // the typed literal still has to match the native bytes.
                    if (index >= instructions.Count || instructions[index].Operands is not
                            [_, _, FloatLiteral literal, _] || !MatchesScale(literal, bits)) return false;
                    coefficient = instructions[index].Operands[2];
                }
                if (!Next(OpCode.FloatMultiply, expected.Arithmetic!.Value.IP, out var multiply) ||
                    !FloatMultiplication.TryGet(multiply, out var precision) || precision.Width != 32 ||
                    !Binary(multiply, converted, coefficient, out result)) return false;
            }
            if (expected.Store is { } store && (!Next(OpCode.Move, store.IP, out var write) || write.Operands is not
                    [FieldReference destination, LocalVariable stored] || !Access(destination, original.First!) ||
                    !ReferenceEquals(stored, result))) return false;
            if (!Next(OpCode.Return, expected.Return.IP, out var returned) || index != instructions.Count) return false;
            return expected.Store != null ? returned.Operands.Count == 0 :
                returned.Operands is [LocalVariable value] && ReferenceEquals(value, result);

            bool Next(OpCode opcode, ulong address, out Instruction operation, int width = 0)
            {
                operation = null!;
                if (index >= instructions.Count) return false;
                operation = instructions[index++];
                return operation.OpCode == opcode && operation.NativeAddress == address &&
                    operation.IntegerBitWidth == width && operation.CallSemantics == CallSemantics.Direct;
            }

            bool Output(LocalVariable local, TypeAnalysisContext type) =>
                ReferenceEquals(local.Type, type) && !method.ParameterLocals.Contains(local) && outputs.Add(local);

            bool Access(FieldReference access, FieldAnalysisContext field) =>
                ReferenceEquals(access.Field, field) && access.Offset == field.Offset && access.Local.IsThis &&
                access.Local.Register == new Register(null, "rcx") && method.ParameterLocals.Contains(access.Local) &&
                ReferenceEquals(access.Local.Type, method.DeclaringType);

            bool Read(NativeInstruction native, FieldAnalysisContext field, out LocalVariable local)
            {
                local = null!;
                if (!Next(OpCode.Move, native.IP, out var read) || read.Operands is not
                        [LocalVariable captured, FieldReference access] || !Access(access, field) ||
                    !Output(captured, field.FieldType)) return false;
                local = captured;
                return true;
            }

            bool Convert(NativeInstruction native, LocalVariable input, out LocalVariable convertedValue)
            {
                convertedValue = null!;
                if (!Next(OpCode.Int32ToSingle, native.IP, out var conversion) ||
                    !IntegerFloatConversion.HasCanonicalTypes(conversion, method.AppContext.SystemTypes) ||
                    conversion.Operands is not [LocalVariable value, LocalVariable source] ||
                    !ReferenceEquals(source, input) || !Output(value, method.AppContext.SystemTypes.SystemSingleType)) return false;
                convertedValue = value;
                return true;
            }

            bool Binary(Instruction operation, LocalVariable left, IOperand right, out LocalVariable computed)
            {
                computed = null!;
                if (operation.Operands is not [LocalVariable value, LocalVariable firstInput, var secondInput, _] ||
                    !ReferenceEquals(firstInput, left) || !ReferenceEquals(secondInput, right) ||
                    !Output(value, method.AppContext.SystemTypes.SystemSingleType)) return false;
                computed = value;
                return true;
            }

            static bool MatchesScale(FloatLiteral literal, uint bits) =>
                BitConverter.ToUInt32(BitConverter.GetBytes(literal.Value), 0) == bits;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static int ParameterFor(MethodAnalysisContext method, NativeRegister source)
    {
        var abi = new X64CallingConventionResolver().ResolveForParameters(method);
        return method.Parameters is [var parameter] &&
               ReferenceEquals(parameter.ParameterType, method.AppContext.SystemTypes.SystemInt32Type) &&
               abi[method.IsStatic ? 0 : 1] is Register register && register.Name == X86Utils.GetRegisterName(source) ? 0 : -1;
    }

    private static FieldAnalysisContext? Field(MethodAnalysisContext method, NativeInstruction native,
        TypeAnalysisContext? expected, bool written)
    {
        if (method.DeclaringType!.Fields.Where(field => field.Offset == (int)native.MemoryDisplacement64).ToArray() is not [var field] ||
            !ReferenceEquals(field.DeclaringType, method.DeclaringType) || field.Name != field.DefaultName ||
            written && (field.Attributes & FieldAttributes.InitOnly) != 0 ||
            expected != null && !ReferenceEquals(field.FieldType, expected)) return null;
        var access = new FieldReference(field, new LocalVariable("receiver", new Register(null, "rcx"), method.DeclaringType), field.Offset);
        if (ReferenceEquals(field.FieldType, method.AppContext.SystemTypes.SystemSingleType))
            return written && NarrowFieldEqualityProof.HasUnchangedFloatingFieldLayout(access, 32) ? field : null;
        if (ReferenceEquals(field.FieldType, method.AppContext.SystemTypes.SystemInt32Type))
            return NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, 32) ? field : null;
        return !written && Enum32StorageProof.IsUnchanged(field.FieldType) &&
               ReferenceEquals(field.FieldType.EnumUnderlyingType, method.AppContext.SystemTypes.SystemInt32Type) &&
               NarrowFieldEqualityProof.HasUnchangedEnum32FieldLayout(access) ? field : null;
    }

    private static bool Signature(MethodAnalysisContext method)
    {
        if (!X86RuntimeNullThrowProof.IsSupportedProfile(method.AppContext) ||
            method.DeclaringType is not { Definition: { GenericContainer: null,
                RawType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0 } } } owner ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
            owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes || !ReferenceEquals(owner.BaseType, owner.DefaultBaseType) ||
            method.Definition is not { GenericContainer: null, IsUnmanagedCallersOnly: false,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_R4 or Il2CppTypeEnum.IL2CPP_TYPE_VOID,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, owner.Definition) || method.IsVirtual ||
            method.Name is ".ctor" or ".cctor" || method.Name != method.DefaultName || method.GenericParameters.Count != 0 ||
            method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
            method.OverrideReturnType != null || !ReferenceEquals(method.ReturnType, method.DefaultReturnType) ||
            (!ReferenceEquals(method.ReturnType, method.AppContext.SystemTypes.SystemSingleType) && !method.IsVoid) ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) != 0 ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) || method.Parameters.Count > 1) return false;
        return method.Parameters.All(parameter => ReferenceEquals(parameter.DeclaringMethod, method) &&
            parameter.ParameterIndex == 0 && !parameter.IsRef && parameter.OverrideParameterType == null &&
            !parameter.UseOverrideDefaultValue && parameter.Name == parameter.DefaultName &&
            parameter.Attributes == parameter.DefaultAttributes &&
            ReferenceEquals(parameter.ParameterType, method.AppContext.SystemTypes.SystemInt32Type) &&
            ReferenceEquals(parameter.ParameterType, parameter.DefaultParameterType) &&
            parameter.Definition?.RawType is { Type: Il2CppTypeEnum.IL2CPP_TYPE_I4, NumMods: 0, Byref: 0, Pinned: 0 });
    }

    private static object[]? Snapshot(MethodAnalysisContext method, params FieldAnalysisContext?[] fields)
    {
        var definition = method.Definition!;
        var values = new List<object> { method.UnderlyingPointer, method.Name, method.Attributes, method.ImplAttributes,
            method.ReturnType, definition.nameIndex, definition.token, definition.flags, definition.iflags, definition.slot,
            definition.returnTypeIdx, definition.declaringTypeIdx, definition.parameterStart, definition.parameterCount };
        Raw(definition.RawReturnType!);
        foreach (var parameter in method.Parameters)
        {
            var raw = parameter.Definition!;
            values.AddRange([parameter, parameter.Name, parameter.Attributes, parameter.ParameterIndex, parameter.ParameterType,
                raw.nameIndex, raw.token, raw.typeIndex]);
            Raw(raw.RawType!);
        }
        var seen = new HashSet<TypeAnalysisContext>();
        for (var owner = method.DeclaringType; owner != null; owner = owner.BaseType)
            if (!seen.Add(owner) || !Capture(owner)) return null;
        foreach (var enumType in fields.OfType<FieldAnalysisContext>().Select(field => field.FieldType).Where(type => type.IsEnumType).Distinct())
        {
            if (!Capture(enumType)) return null;
            values.Add(enumType.EnumUnderlyingType!);
            Raw(enumType.Definition!.EnumUnderlyingType!);
        }
        return values.ToArray();

        bool Capture(TypeAnalysisContext type)
        {
            if (type.Name != type.DefaultName || type.Namespace != type.DefaultNamespace || type.Attributes != type.DefaultAttributes ||
                !ReferenceEquals(type.BaseType, type.DefaultBaseType) || type.Definition is not { } rawType ||
                type.Fields.Count != rawType.FieldCount) return false;
            values.AddRange([type, type.DeclaringAssembly, type.Name, type.Namespace, type.Attributes,
                type.BaseType ?? (object)"no-base", type.DeclaringType ?? (object)"no-enclosing-type",
                rawType.NameIndex, rawType.NamespaceIndex, rawType.Token, rawType.Flags, rawType.Bitfield,
                rawType.ByvalTypeIndex, rawType.ParentIndex, rawType.DeclaringTypeIndex, rawType.GenericContainerIndex,
                rawType.RawSizes.instance_size, rawType.FieldCount]);
            Raw(rawType.RawType);
            if (rawType.RawBaseType is { } rawBase) Raw(rawBase);
            foreach (var field in type.Fields)
            {
                if (field.Name != field.DefaultName || field.Attributes != field.DefaultAttributes || field.Offset != field.DefaultOffset ||
                    field.OverrideFieldType != null || field.BackingData?.Field.RawFieldType is not { } raw) return false;
                values.AddRange([field, field.Name, field.Attributes, field.Offset, field.FieldType,
                    field.BackingData.Field.nameIndex, field.BackingData.Field.token, field.BackingData.Field.typeIndex]);
                Raw(raw);
            }
            return true;
        }

        void Raw(Il2CppType raw) => values.AddRange([raw.Bits, raw.Datapoint, raw.Data.Dummy,
            raw.Attrs, raw.Type, raw.NumMods, raw.Byref, raw.Pinned, raw.ValueType]);
    }

    internal static Shape? TryProveShape(IReadOnlyList<NativeInstruction> body)
    {
        // MOVD supplies only a signed low32 value and zeros the other three
        // lanes. Every following CVTDQ2PS is register-only, and the closed
        // bodies observe only the low lane through scalar arithmetic/store or
        // return. Packed conversions cannot introduce an extra memory read.
        if (body.Count is < 3 or > 8 || body.Any(native => native.IsInvalid || native.CodeSize != CodeSize.Code64 ||
                native.HasLockPrefix || native.HasRepPrefix || native.HasRepnePrefix || native.SegmentPrefix != NativeRegister.None) ||
            body.Where((native, index) => index > 0 && native.IP != body[index - 1].NextIP).Any() ||
            body[^1] is not { Code: Code.Retnq, OpCount: 0, Length: 1 }) return null;
        var start = body[0] is { Code: Code.Nopw, Length: 2, OpCount: 0 } ? 1 : 0;
        var instructions = body.Skip(start).ToArray();
        if (instructions.Length is 3 or 4 && instructions[0] is
                { Code: Code.Movd_xmm_rm32, Op0Kind: OpKind.Register, Op0Register: NativeRegister.XMM0,
                    Op1Kind: OpKind.Register, Op1Register: NativeRegister.ECX or NativeRegister.EDX, OpCount: 2 } parameter &&
            Conversion(instructions[1], NativeRegister.XMM0))
        {
            NativeInstruction? store = null;
            if (instructions.Length == 4)
            {
                store = instructions[2];
                if (store.Value.Code != Code.Movss_xmmm32_xmm || !Memory(store.Value, 0) ||
                    store.Value.Op1Kind != OpKind.Register || store.Value.Op1Register != NativeRegister.XMM0) return null;
            }
            return new(Kind.Parameter, parameter, instructions[1], null, null, null, null, store, body[^1]);
        }
        if (instructions.Length == 6 && instructions[0] is
                { Code: Code.Mov_r32_rm32, Op0Kind: OpKind.Register, Op0Register: NativeRegister.EAX } read && Memory(read, 1) &&
            instructions[1] is { Code: Code.Dec_rm32, OpCount: 1, Op0Kind: OpKind.Register, Op0Register: NativeRegister.EAX } decrement &&
            Registers(instructions[2], Code.Movd_xmm_rm32, NativeRegister.XMM0, NativeRegister.EAX) &&
            Conversion(instructions[3], NativeRegister.XMM0) && instructions[4] is
                { Code: Code.Mulss_xmm_xmmm32, OpCount: 2, Op0Kind: OpKind.Register, Op0Register: NativeRegister.XMM0,
                    Op1Kind: OpKind.Memory, MemorySize: MemorySize.Float32, MemoryIndex: NativeRegister.None,
                    MemoryBase: NativeRegister.RIP, MemoryIndexScale: 1, IsIPRelativeMemoryOperand: true } arithmetic)
            return new(Kind.ScaledField, read, instructions[3], null, null, decrement, arithmetic, null, body[^1]);
        if (instructions.Length == 6 && instructions[0] is
                { Code: Code.Movd_xmm_rm32, Op0Kind: OpKind.Register, Op0Register: NativeRegister.XMM0 } first && Memory(first, 1) &&
            instructions[1] is { Code: Code.Movd_xmm_rm32, Op0Kind: OpKind.Register, Op0Register: NativeRegister.XMM1 } second && Memory(second, 1) &&
            Conversion(instructions[2], NativeRegister.XMM0) && Conversion(instructions[3], NativeRegister.XMM1) &&
            Registers(instructions[4], Code.Divss_xmm_xmmm32, NativeRegister.XMM0, NativeRegister.XMM1))
            return new(Kind.FieldRatio, first, instructions[2], second, instructions[3], null, instructions[4], null, body[^1]);
        return null;

        static bool Conversion(NativeInstruction instruction, NativeRegister register) =>
            Registers(instruction, Code.Cvtdq2ps_xmm_xmmm128, register, register);
        static bool Registers(NativeInstruction instruction, Code code, NativeRegister left, NativeRegister right) =>
            instruction.Code == code && instruction.FlowControl == FlowControl.Next && instruction.OpCount == 2 &&
            instruction.Op0Kind == OpKind.Register && instruction.Op1Kind == OpKind.Register &&
            instruction.Op0Register == left && instruction.Op1Register == right;
        static bool Memory(NativeInstruction native, int operand) =>
            native.OpCount == 2 && native.GetOpKind(operand) == OpKind.Memory &&
            native.MemoryBase == NativeRegister.RCX && native.MemoryIndex == NativeRegister.None &&
            native.MemoryIndexScale == 1 && native.MemorySize.GetSize() == 4 &&
            native.MemoryDisplacement64 is >= 16 and < 4096 && native.MemoryDisplacement64 % 4 == 0;
    }
}
