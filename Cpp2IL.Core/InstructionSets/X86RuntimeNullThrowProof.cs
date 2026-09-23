using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Recognizes the exact installed NullCheck runtime operation and its nonreturning native
/// path. The constructor identifies the corlib class; it is NOT an ordinary newobj contract.
/// Runtime::ObjectInit invokes this constructor with managed exceptions suppressed. Preserve
/// that behavior through a same-profile implicit NullCheck, never explicit new+throw.
/// The caller condition, receiver identity, and effect ordering require a separate proof.
/// </summary>
internal static class X86RuntimeNullThrowProof
{
    internal enum Frame { Leaf, Stack28, Stack38, SavedRbxRdiStack20 }
    internal readonly record struct Region(ulong Start, ulong End, Frame Prolog);

    public static RuntimeNullThrowEvidence? TryIdentify(ApplicationAnalysisContext app, ulong target)
        => RuntimeNullThrowEvidence.TryCreate(app, target);

    internal static MethodAnalysisContext? TryResolveIdentity(ApplicationAnalysisContext app, ulong target)
    {
        if (!IsSupportedProfile(app) || app.Binary is not PE pe)
            return null;
        try
        {
            if (!TryProve(target, Read, pe.GetVirtualAddressOfExportedFunctionByName,
                    address => ReadReadOnlyName(pe.GetRawBinaryContent(), X64UnwindProof.ForApplication(app), address),
                    regions => AllowsUnwind(X64UnwindProof.ForApplication(app), regions)))
                return null;
            return BindIdentity(app);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                          IndexOutOfRangeException or OverflowException or EndOfStreamException)
        {
            return null;
        }

        IReadOnlyList<Instruction>? Read(ulong address, int count)
        {
            // Stay in the native code section and decode only the small, requested prefix.
            // A proved RET or nonreturning API closes that path, not the end of this byte span.
            var start = pe.GetVirtualAddressOfPrimaryExecutableSection();
            var code = pe.GetEntirePrimaryExecutableSection();
            if (address < start || address - start >= (ulong)code.Length)
                return null;
            var offset = checked((int)(address - start));
            var bytes = code.Slice(offset, Math.Min(count * 15, code.Length - offset)).ToArray();
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), address);
            var instructions = new Instruction[count];
            for (var i = 0; i < count; i++)
                instructions[i] = decoder.Decode();
            return instructions;
        }
    }

    internal static string? ReadReadOnlyName(ReadOnlySpan<byte> image, X64UnwindProof.Index? index, ulong address)
    {
        if (index == null)
            return null;
        var offset = index.MapReadOnlyData(address, 1);
        if (offset < 0)
            return null;
        for (var length = 0; length <= 64; length++)
        {
            if (index.MapReadOnlyData(address, (uint)length + 1) != offset || offset > image.Length - length - 1)
                return null;
            var value = image[offset + length];
            if (value == 0)
                return length == 0 ? null : System.Text.Encoding.ASCII.GetString(image.Slice(offset, length).ToArray());
            if (value < 32 || value >= 127)
                return null;
        }
        return null;
    }

    internal static bool IsSupportedProfile(ApplicationAnalysisContext app)
        => app.Binary is PE { PointerSizeBytes: 8 } pe && pe.InstructionSetId == DefaultInstructionSets.X86_64 &&
           app.UnityVersion.ToString() == "2021.3.35f1";

    internal static MethodAnalysisContext? BindIdentity(ApplicationAnalysisContext app, string exceptionTypeName = "NullReferenceException")
    {
        var corlib = app.SystemTypes.SystemObjectType.DeclaringAssembly;
        if (corlib.Definition == null || corlib.Name != corlib.DefaultName || corlib.DefaultName != "mscorlib" ||
            corlib.Version != corlib.DefaultVersion || (corlib.Culture ?? "") != (corlib.DefaultCulture ?? "") ||
            corlib.Flags != corlib.DefaultFlags ||
            !(corlib.PublicKey ?? []).SequenceEqual(corlib.DefaultPublicKey ?? []) ||
            !(corlib.PublicKeyToken ?? []).SequenceEqual(corlib.DefaultPublicKeyToken ?? []))
            return null;
        var types = corlib.Types.Where(t => t.Name == exceptionTypeName && t.Namespace == "System").ToArray();
        if (types is not [var type] || type.DeclaringType != null ||
            type.Definition is not { GenericContainer: null, DeclaringType: null } ||
            type.Name != type.DefaultName || type.Namespace != type.DefaultNamespace ||
            type.Attributes != type.DefaultAttributes || !ReferenceEquals(type.BaseType, type.DefaultBaseType) ||
            type.IsValueType || type.IsEnumType || type.IsGenericInstance || type.GenericParameters.Count != 0 ||
            !ReferenceEquals(type.DeclaringAssembly, corlib) || !ReferenceEquals(type.Definition.DeclaringAssembly, corlib.Definition.Image))
            return null;
        var constructors = type.Methods.Where(m => m.Name == ".ctor" && m.Parameters.Count == 0).ToArray();
        if (constructors is not [var constructor] ||
            constructor.Definition is not { parameterCount: 0, GenericContainer: null } definition ||
            constructor.Name != constructor.DefaultName || !ReferenceEquals(constructor.DeclaringType, type) ||
            !ReferenceEquals(definition.DeclaringType, type.Definition) ||
            constructor.GenericParameters.Count != 0 || constructor.Attributes != constructor.DefaultAttributes ||
            constructor.ImplAttributes != constructor.DefaultImplAttributes ||
            definition.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_VOID, NumMods: 0, Byref: 0, Pinned: 0 } ||
            (constructor.Attributes & (MethodAttributes.MemberAccessMask | MethodAttributes.Static | MethodAttributes.Abstract |
                                       MethodAttributes.PinvokeImpl | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName)) !=
            (MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName) ||
            (constructor.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask | MethodImplAttributes.InternalCall)) != 0 ||
            !ReferenceEquals(constructor.ReturnType, app.SystemTypes.SystemVoidType) ||
            !ReferenceEquals(constructor.ReturnType, constructor.DefaultReturnType))
            return null;
        return constructor;
    }

    internal static bool TryProve(ulong target, Func<ulong, int, IReadOnlyList<Instruction>?> read,
        Func<string, ulong> export, Func<ulong, string?> readString,
        Func<IReadOnlyList<Region>, bool> allowsUnwind)
    {
        // These exact exported ABI contracts identify the runtime operations independently
        // of the candidate helper. Do not use cached key-function guesses or follow arbitrary
        // call graphs. ObjectNew's export catches exceptions, so anchor its internal allocator
        // through the checked wrapper; do not replace it with the catching embedding API.
        var corlib = Thunk("il2cpp_get_corlib");
        var lookup = Thunk("il2cpp_class_from_name");
        var initialize = Thunk("il2cpp_runtime_object_init");
        var allocatorExport = Read(export("il2cpp_object_new"), 3);
        if (corlib == 0 || lookup == 0 || initialize == 0 || allocatorExport == null ||
            !Stack(allocatorExport[0], Mnemonic.Sub, 0x28) || !Call(allocatorExport[1]) || !Jump(allocatorExport[2]))
            return false;
        var allocatorExit = Read(allocatorExport[2].NearBranchTarget, 2);
        if (allocatorExit == null || !Stack(allocatorExit[0], Mnemonic.Add, 0x28) || !Return(allocatorExit[1]))
            return false;
        var allocate = allocatorExport[1].NearBranchTarget;

        // The installed runtime declares Exception::Raise NORETURN. Its exported wrapper
        // supplies lastManagedFrame=null and forwards RCX unchanged to this exact target.
        var raiserExport = Read(export("il2cpp_raise_exception"), 3);
        if (raiserExport == null || !Stack(raiserExport[0], Mnemonic.Sub, 0x28) ||
            !Registers(raiserExport[1], Mnemonic.Xor, Register.EDX, Register.EDX) || !Call(raiserExport[2]))
            return false;
        var raise = raiserExport[2].NearBranchTarget;
        if (new[] { corlib, lookup, initialize, allocate, raise }.Distinct().Count() != 5)
            return false;

        // Codegen wrapper -> empty-message wrapper -> initialize fresh StringView -> raiser.
        // Each call target below has a separately checked body, not a transitive-name match.
        var wrapper = Read(target, 2);
        if (wrapper == null || !Stack(wrapper[0], Mnemonic.Sub, 0x28) || !Call(wrapper[1]))
            return false;
        var empty = Read(wrapper[1].NearBranchTarget, 5);
        if (empty == null || !Stack(empty[0], Mnemonic.Sub, 0x38) ||
            !LoadAddress(empty[1], Register.RCX, Register.RSP, 0x20) || !Call(empty[2]) ||
            !Registers(empty[3], Mnemonic.Mov, Register.RCX, Register.RAX) || !Call(empty[4]))
            return false;
        var zero = Read(empty[2].NearBranchTarget, 5);
        if (zero == null || !Registers(zero[0], Mnemonic.Xor, Register.EAX, Register.EAX) ||
            !Store(zero[1], Register.RCX, 0, Register.RAX) || !Store(zero[2], Register.RCX, 8, Register.RAX) ||
            !Registers(zero[3], Mnemonic.Mov, Register.RAX, Register.RCX) || !Return(zero[4]))
            return false;
        var constructAndRaise = Read(empty[4].NearBranchTarget, 5);
        if (constructAndRaise == null || !Stack(constructAndRaise[0], Mnemonic.Sub, 0x28) ||
            !Call(constructAndRaise[1]) || !Registers(constructAndRaise[2], Mnemonic.Mov, Register.RCX, Register.RAX) ||
            !Registers(constructAndRaise[3], Mnemonic.Xor, Register.EDX, Register.EDX) ||
            !Call(constructAndRaise[4], raise))
            return false;

        // Prove all executed factory effects: resolve this exact corlib class, allocate it,
        // invoke the runtime ObjectInit contract once, and return that same object. That
        // API suppresses managed constructor exceptions; no ordinary-newobj equivalence is
        // claimed. RDI retains
        // the private zeroed view and is never passed to another operation. Thus [RDI+8]==0
        // remains established, and the message-write arm cannot execute in this composition.
        var factory = Read(constructAndRaise[1].NearBranchTarget, 16);
        if (factory == null || !Store(factory[0], Register.RSP, 8, Register.RBX) ||
            !UnaryRegister(factory[1], Mnemonic.Push, Register.RDI) || !Stack(factory[2], Mnemonic.Sub, 0x20) ||
            !Registers(factory[3], Mnemonic.Mov, Register.RDI, Register.RCX) || !Call(factory[4], corlib) ||
            !LiteralAddress(factory[5], Register.R8, "NullReferenceException") ||
            !Registers(factory[6], Mnemonic.Mov, Register.RCX, Register.RAX) ||
            !LiteralAddress(factory[7], Register.RDX, "System") || !Call(factory[8], lookup) ||
            !Registers(factory[9], Mnemonic.Mov, Register.RCX, Register.RAX) || !Call(factory[10], allocate) ||
            !Registers(factory[11], Mnemonic.Mov, Register.RCX, Register.RAX) ||
            !Registers(factory[12], Mnemonic.Mov, Register.RBX, Register.RAX) || !Call(factory[13], initialize) ||
            factory[14].Mnemonic != Mnemonic.Cmp || factory[14].OpCount != 2 || !Memory(factory[14], 0, Register.RDI, 8) ||
            factory[14].Op1Kind is not (OpKind.Immediate8to64 or OpKind.Immediate32to64) || factory[14].GetImmediate(1) != 0 ||
            factory[15].Mnemonic != Mnemonic.Jbe || factory[15].Op0Kind != OpKind.NearBranch64 ||
            factory[15].NearBranchTarget < factory[15].NextIP || factory[15].NearBranchTarget - factory[15].NextIP > 64)
            return false;
        var exit = Read(factory[15].NearBranchTarget, 5);
        if (exit == null || !Registers(exit[0], Mnemonic.Mov, Register.RAX, Register.RBX) ||
            !Load(exit[1], Register.RBX, Register.RSP, 0x30) || !Stack(exit[2], Mnemonic.Add, 0x20) ||
            !UnaryRegister(exit[3], Mnemonic.Pop, Register.RDI) || !Return(exit[4]))
            return false;
        // All executed candidate frames must propagate exceptions. The exported allocator's
        // catch arm is only an API anchor and is not one of these executed functions.
        return allowsUnwind([
            new(wrapper[0].IP, wrapper[^1].NextIP, Frame.Stack28),
            new(empty[0].IP, empty[^1].NextIP, Frame.Stack38),
            new(zero[0].IP, zero[^1].NextIP, Frame.Leaf),
            new(constructAndRaise[0].IP, constructAndRaise[^1].NextIP, Frame.Stack28),
            new(factory[0].IP, exit[^1].NextIP, Frame.SavedRbxRdiStack20),
        ]);

        IReadOnlyList<Instruction>? Read(ulong address, int count)
        {
            if (address == 0 || read(address, count) is not { } body || body.Count != count)
                return null;
            var next = address;
            foreach (var instruction in body)
            {
                if (instruction.IP != next || instruction.Length == 0 || instruction.IsInvalid ||
                    instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix || instruction.HasRepPrefix ||
                    instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None)
                    return null;
                next = instruction.NextIP;
            }
            return body;
        }

        ulong Thunk(string name)
        {
            var body = Read(export(name), 1);
            return body != null && Jump(body[0]) ? body[0].NearBranchTarget : 0;
        }

        bool LiteralAddress(Instruction instruction, Register destination, string value)
            => instruction.Mnemonic == Mnemonic.Lea && instruction.OpCount == 2 &&
               instruction.Op0Kind == OpKind.Register && instruction.Op0Register == destination &&
               instruction.Op1Kind == OpKind.Memory && instruction.MemoryBase == Register.RIP &&
               instruction.MemoryIndex == Register.None && readString(instruction.IPRelativeMemoryAddress) == value;
    }

    internal static bool AllowsUnwind(X64UnwindProof.Index? index, IReadOnlyList<Region> regions)
    {
        if (index == null || regions.Count == 0)
            return false;
        foreach (var region in regions)
        {
            var span = index.ClassifySpan(region.Start, region.End);
            if (span.Kind == X64UnwindProof.SpanKind.NoEntry && region.Prolog == Frame.Leaf)
                continue; // The complete zero-initializer leaf was proved separately.
            var codes = region.Prolog switch
            {
                Frame.Leaf => Array.Empty<byte>(),
                Frame.Stack28 => new byte[] { 4, 0x42 },
                Frame.Stack38 => new byte[] { 4, 0x62 },
                Frame.SavedRbxRdiStack20 => new byte[] { 10, 0x34, 6, 0, 10, 0x32, 6, 0x70 },
                _ => null,
            };
            var prolog = region.Prolog == Frame.Leaf ? 0 : region.Prolog == Frame.SavedRbxRdiStack20 ? 10 : 4;
            if (codes == null || !index.MatchesUnwind(region.Start, region.End, (byte)prolog, 0, codes))
                return false;
        }
        return true;
    }

    private static bool Registers(Instruction i, Mnemonic mnemonic, Register destination, Register source)
        => i.Mnemonic == mnemonic && i.OpCount == 2 && i.Op0Kind == OpKind.Register && i.Op1Kind == OpKind.Register &&
           i.Op0Register == destination && i.Op1Register == source;

    private static bool UnaryRegister(Instruction i, Mnemonic mnemonic, Register register)
        => i.Mnemonic == mnemonic && i.OpCount == 1 && i.Op0Kind == OpKind.Register && i.Op0Register == register;

    private static bool Stack(Instruction i, Mnemonic mnemonic, ulong size)
        => i.Mnemonic == mnemonic && i.OpCount == 2 && i.Op0Kind == OpKind.Register && i.Op0Register == Register.RSP &&
           i.Op1Kind is OpKind.Immediate8to64 or OpKind.Immediate32to64 && i.GetImmediate(1) == size;

    private static bool Memory(Instruction i, int operand, Register register, ulong displacement)
        => i.GetOpKind(operand) == OpKind.Memory && i.MemoryBase == register && i.MemoryIndex == Register.None &&
           i.MemoryDisplacement64 == displacement && i.MemorySize.GetSize() == 8;

    private static bool Store(Instruction i, Register address, ulong displacement, Register value)
        => i.Mnemonic == Mnemonic.Mov && i.OpCount == 2 && Memory(i, 0, address, displacement) &&
           i.Op1Kind == OpKind.Register && i.Op1Register == value;

    private static bool Load(Instruction i, Register value, Register address, ulong displacement)
        => i.Mnemonic == Mnemonic.Mov && i.OpCount == 2 && i.Op0Kind == OpKind.Register && i.Op0Register == value &&
           Memory(i, 1, address, displacement);

    private static bool LoadAddress(Instruction i, Register value, Register address, ulong displacement)
        => i.Mnemonic == Mnemonic.Lea && i.OpCount == 2 && i.Op0Kind == OpKind.Register && i.Op0Register == value &&
           i.Op1Kind == OpKind.Memory && i.MemoryBase == address && i.MemoryIndex == Register.None && i.MemoryDisplacement64 == displacement;

    private static bool Call(Instruction i, ulong target = 0)
        => i.Code == Code.Call_rel32_64 && i.Op0Kind == OpKind.NearBranch64 &&
           i.NearBranchTarget != 0 && (target == 0 || i.NearBranchTarget == target);

    private static bool Jump(Instruction i)
        => i.Mnemonic == Mnemonic.Jmp && i.OpCount == 1 && i.Op0Kind == OpKind.NearBranch64 && i.NearBranchTarget != 0;

    private static bool Return(Instruction i) => i.Code == Code.Retnq && i.OpCount == 0;
}
