using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using NativeRegister = Iced.Intel.Register;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64GuardedArrayOperationProof
{
    internal sealed record InvocationArgument(ulong CallIp, MethodAnalysisContext Target, int ParameterIndex,
        int Bits, ValueOrigin? Origin, long? Literal, FieldAnalysisContext? Field = null,
        NativeRegister FieldOwner = NativeRegister.None, MethodAnalysisContext? Producer = null,
        bool ArrayRead = false);

    // Ordered calls retain more than their target and receiver check. Authenticate
    // every managed argument, including the receiver, and the native MethodInfo.
    // Stack arguments and unproved arithmetic producers stay outside this shape.
    private static bool TryInvocationArguments(MethodAnalysisContext caller, Facts facts, NativeEvidence native,
        IReadOnlyList<Site> sites, ISet<ulong> noReturn, out InvocationArgument[] arguments)
    {
        arguments = [];
        if (X64NativeInvocationValues.Create(facts.Body, noReturn) is not { } values) return false;
        var results = new List<InvocationArgument>();
        foreach (var index in native.Effects.Where(index => facts.Body[index].FlowControl == FlowControl.Call))
        {
            var call = facts.Body[index];
            var target = caller.AppContext.MethodsByAddress[call.NearBranchTarget].Single();
            var first = target.IsStatic ? 0 : -1;
            var managedCount = target.Parameters.Count - first;
            if (managedCount >= Arguments.Length || !values.Matches(call.IP, Arguments[managedCount], 64,
                    new(NativeRegister.None, Literal: 0))) return false;
            for (var parameter = first; parameter < target.Parameters.Count; parameter++)
            {
                var type = parameter < 0 ? target.DeclaringType! : target.Parameters[parameter].ParameterType;
                var bits = ArgumentBits(type);
                if (bits == 0 || BindInvocationArgument(caller, facts, values, sites, index,
                        target, parameter, Arguments[parameter - first], type, bits) is not { } argument)
                    return false;
                results.Add(argument);
            }
        }
        arguments = results.ToArray();
        return true;
    }

    internal static int ArgumentBits(TypeAnalysisContext type) => type.Type switch
    {
        Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_I1 or Il2CppTypeEnum.IL2CPP_TYPE_U1 => 8,
        Il2CppTypeEnum.IL2CPP_TYPE_CHAR or Il2CppTypeEnum.IL2CPP_TYPE_I2 or Il2CppTypeEnum.IL2CPP_TYPE_U2 => 16,
        Il2CppTypeEnum.IL2CPP_TYPE_I4 or Il2CppTypeEnum.IL2CPP_TYPE_U4 => 32,
        Il2CppTypeEnum.IL2CPP_TYPE_I8 or Il2CppTypeEnum.IL2CPP_TYPE_U8 => 64,
        _ => NullCheckedCall.IsReferenceClass(type) || type is SzArrayTypeAnalysisContext ? 64 : 0,
    };

    private static InvocationArgument? BindInvocationArgument(MethodAnalysisContext caller, Facts facts,
        X64NativeInvocationValues values, IReadOnlyList<Site> sites, int before,
        MethodAnalysisContext target, int parameter, NativeRegister register, TypeAnalysisContext type, int bits)
    {
        var callIp = facts.Body[before].IP;
        foreach (var incoming in Arguments)
            if (NullCheckedCall.SameOrdinaryType(EntryType(caller, incoming), type) &&
                values.Matches(callIp, register, bits, new(incoming)))
                return new(callIp, target, parameter, bits, new(incoming, null), null);

        var writer = bits == 64 ? facts.TraceCopies(before, register, out var sourceRegister) :
            facts.TraceScalarCopies(before, register, out sourceRegister);
        if (writer < 0) return null;
        var source = facts.Body[writer];
        ulong? candidate = source.Mnemonic == Mnemonic.Mov && source.Op1Kind is OpKind.Immediate8 or
            OpKind.Immediate16 or OpKind.Immediate32 or OpKind.Immediate64 or OpKind.Immediate32to64
                ? source.GetImmediate(1) :
            source.Mnemonic == Mnemonic.Xor && source.Op0Kind == OpKind.Register && source.Op1Kind == OpKind.Register &&
            source.Op0Register == source.Op1Register ? 0UL :
            source.Code == Code.Lea_r32_m ? source.MemoryDisplacement64 : null;
        if (candidate is { } literal && TryArgumentLiteral(type, literal, out var managed) &&
            values.Matches(callIp, register, bits, new(NativeRegister.None, Literal: unchecked((ulong)managed))))
            return new(callIp, target, parameter, bits, null, managed);

        if (!values.Matches(callIp, register, bits, new(NativeRegister.None, source.IP, sourceRegister)))
            return null;
        var origin = new ValueOrigin(sourceRegister, source.IP);
        if (sites.SingleOrDefault(site => !site.IsStore && site.OperationIp == source.IP) is { } read &&
            NullCheckedCall.SameOrdinaryType(read.ElementType, type) && read.Width * 8 == bits)
            return new(callIp, target, parameter, bits, origin, null, ArrayRead: true);
        if (source.Code == Code.Call_rel32_64 && sourceRegister == NativeRegister.RAX &&
            caller.AppContext.MethodsByAddress.TryGetValue(source.NearBranchTarget, out var bindings) &&
            bindings is [var producer] && HasEligibleEffectCall(producer) &&
            NullCheckedCall.SameOrdinaryType(producer.ReturnType, type))
            return new(callIp, target, parameter, bits, origin, null, Producer: producer);
        if (source.Mnemonic is not (Mnemonic.Mov or Mnemonic.Movzx or Mnemonic.Movsx) ||
            source.Op0Kind != OpKind.Register || source.Op0Register.GetSize() * 8 < bits ||
            source.Op1Kind != OpKind.Memory || source.MemoryIndex != NativeRegister.None ||
            source.MemorySize.GetSize() * 8 != bits || source.MemoryDisplacement64 > int.MaxValue ||
            facts.TraceCopies(writer, source.MemoryBase, out var ownerEntry) >= 0 ||
            EntryType(caller, ownerEntry) is not { } owner || !OrdinaryClass(owner) ||
            owner.Fields.Where(field => !field.IsStatic && field.Offset == (int)source.MemoryDisplacement64 &&
                NullCheckedCall.SameOrdinaryType(field.FieldType, type)).ToArray() is not [var field] ||
            !AccessibleField(field, caller.DeclaringType!)) return null;
        var access = new FieldReference(field, new LocalVariable("argument-owner", new ISIL.Register(null, "owner"), owner), field.Offset);
        if (!(type.IsValueType ? NarrowFieldEqualityProof.HasUnchangedFieldLayout(access, bits) :
                  NarrowFieldEqualityProof.HasUnchangedReferenceFieldLayout(access))) return null;
        return new(callIp, target, parameter, bits, origin, null, field, ownerEntry);
    }

    internal static bool TryArgumentLiteral(TypeAnalysisContext type, ulong bits, out long value)
    {
        value = type.Type switch
        {
            Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN or Il2CppTypeEnum.IL2CPP_TYPE_U1 => (byte)bits,
            Il2CppTypeEnum.IL2CPP_TYPE_I1 => unchecked((sbyte)bits),
            Il2CppTypeEnum.IL2CPP_TYPE_CHAR or Il2CppTypeEnum.IL2CPP_TYPE_U2 => (ushort)bits,
            Il2CppTypeEnum.IL2CPP_TYPE_I2 => unchecked((short)bits),
            Il2CppTypeEnum.IL2CPP_TYPE_U4 => (uint)bits,
            Il2CppTypeEnum.IL2CPP_TYPE_I4 => unchecked((int)bits),
            _ => unchecked((long)bits),
        };
        return type.IsValueType ? ArgumentBits(type) != 0 &&
            (type.Type != Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN || value is 0 or 1) : bits == 0;
    }
}
