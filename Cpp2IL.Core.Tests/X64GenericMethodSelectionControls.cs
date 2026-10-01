using Iced.Intel;
using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.InstructionSets;

namespace Cpp2IL.Core.Tests;

internal static class X64GenericMethodSelectionControls
{
    private readonly record struct Key(ulong Definition, ulong Class, ulong Method);
    private sealed class ByteWriter : CodeWriter
    {
        internal List<byte> Bytes { get; } = [];
        public override void WriteByte(byte value) => Bytes.Add(value);
    }

    private static byte[] Replace(byte[] body, ulong entry, int ordinal, Func<Instruction, Instruction> edit)
    {
        var c = X64GenericMethodSelectionRecipe.Decode(entry, body);
        var original = c[ordinal];
        var writer = new ByteWriter();
        var encoder = Encoder.Create(64, writer);
        encoder.Encode(edit(original), original.IP);
        if (writer.Bytes.Count != original.Length) throw new InvalidOperationException("Mutation changed instruction length.");
        var changed = (byte[])body.Clone();
        writer.Bytes.CopyTo(changed, checked((int)(original.IP - entry)));
        return changed;
    }

    private sealed class Machine
    {
        private readonly Dictionary<Register, ulong> _registers = [];
        private readonly Dictionary<ulong, byte> _memory = [];
        private readonly Dictionary<ulong, int> _instructions;
        private readonly Instruction[] _code;
        private readonly X64GenericMethodSelectionRecipe.Evidence _recipe;
        private readonly Dictionary<Key, ulong> _map = [];
        private readonly ulong _class, _method, _sharedClass, _sharedMethod, _fullClass, _fullMethod;
        private readonly ulong _end;
        private readonly ulong _seed;
        private readonly bool _makeReturnsZero;
        private bool _zero;
        internal List<string> Calls { get; } = [];
        internal List<Key> Lookups { get; } = [];
        internal List<(ulong Container, ulong Context)> FullySharedArguments { get; } = [];
        internal List<ulong> SharedArguments { get; } = [];
        internal bool? MakeFlag { get; private set; }
        private const ulong Definition = 0x21000, Context = 0x22000, Class = 0x23000;
        private const ulong Result = 0x24000, Nodes = 0x25000, ClassContainer = 0x26000, MethodContainer = 0x27000;

        internal Machine(ulong entry, byte[] body, X64GenericMethodSelectionRecipe.Evidence recipe,
            int contexts, int registrations, int seed, ulong capacity, bool makeReturnsZero = false,
            ulong entryStackPointer = 0x100008)
        {
            _recipe = recipe;
            _code = X64GenericMethodSelectionRecipe.Decode(entry, body);
            _instructions = _code.Select((i, n) => (i.IP, n)).ToDictionary(pair => pair.IP, pair => pair.n);
            _class = (contexts & 1) != 0 ? 0x31000ul : 0;
            _method = (contexts & 2) != 0 ? 0x32000ul : 0;
            _sharedClass = _class == 0 ? 0ul : 0x33000ul;
            _sharedMethod = _method == 0 ? 0ul : 0x34000ul;
            _fullClass = _class == 0 ? 0ul : 0x35000ul;
            _fullMethod = _method == 0 ? 0ul : 0x36000ul;
            _end = Nodes + capacity * 24;
            _seed = (ulong)seed;
            _makeReturnsZero = makeReturnsZero;
            if (capacity != 0)
            {
                if ((registrations & 1) != 0) _map.TryAdd(new Key(Definition, _class, _method), 1);
                if ((registrations & 2) != 0) _map.TryAdd(new Key(Definition, _sharedClass, _sharedMethod), 2);
                if ((registrations & 4) != 0) _map.TryAdd(new Key(Definition, _fullClass, _fullMethod), 3);
            }
            Set(Register.RSP, entryStackPointer);
            Set(Register.RBP, 0xaa000);
            Set(Register.RBX, 0xbb000);
            Set(Register.RSI, 0xcc000);
            Set(Register.RDI, 0xdd000);
            Set(Register.R14, 0xee000);
            Set(Register.RCX, Result);
            Set(Register.RDX, Definition);
            Set(Register.R8, Context);
            Write(Context, _class, 8); Write(Context + 8, _method, 8);
            Write(Definition + 32, Class, 8);
            Write(Class + 240, _class == 0 ? 0 : ClassContainer, 8);
            Write(Definition + 64, _method == 0 ? 0 : MethodContainer, 8);
            Write(recipe.MethodTableMap + 72, capacity, 8);
            Write(recipe.MethodTableMap + 104, Nodes, 8);
            for (ulong n = 0; n < 4; n++) Write(Result + n * 8, ulong.MaxValue, 8);
            Write(Get(Register.RSP), 0xf0000, 8);
        }

        private static (Register Full, int Width) Alias(Register register) => register switch
        {
            Register.EAX => (Register.RAX, 4), Register.R8D => (Register.R8, 4),
            Register.R14D => (Register.R14, 4), Register.R8L => (Register.R8, 1),
            Register.RAX or Register.RBX or Register.RCX or Register.RDX or Register.RSP or Register.RBP or
                Register.RSI or Register.RDI or Register.R8 or Register.R9 or Register.R10 or Register.R11 or Register.R14
                => (register, 8),
            _ => throw new InvalidOperationException("Unmodeled register: " + register)
        };
        private ulong Get(Register register)
        {
            var (full, width) = Alias(register);
            var value = _registers.GetValueOrDefault(full);
            return width == 8 ? value : width == 4 ? (uint)value : (byte)value;
        }
        private void Set(Register register, ulong value)
        {
            var (full, width) = Alias(register);
            _registers[full] = width == 8 ? value : width == 4 ? (uint)value :
                (_registers.GetValueOrDefault(full) & ~255ul) | (byte)value;
        }
        private ulong Address(Instruction i) => i.IsIPRelativeMemoryOperand ? i.IPRelativeMemoryAddress :
            unchecked(Get(i.MemoryBase) + (i.MemoryIndex == Register.None ? 0 : Get(i.MemoryIndex) * (ulong)i.MemoryIndexScale) + i.MemoryDisplacement64);
        private ulong Read(ulong address, int width)
        {
            ulong result = 0;
            for (var n = 0; n < width; n++) result |= (ulong)_memory[address + (ulong)n] << (8 * n);
            return result;
        }
        private void Write(ulong address, ulong value, int width)
        {
            for (var n = 0; n < width; n++) _memory[address + (ulong)n] = (byte)(value >> (8 * n));
        }
        private ulong Operand(Instruction i, int operand) => i.GetOpKind(operand) switch
        {
            OpKind.Register => Get(i.GetOpRegister(operand)),
            OpKind.Memory => Read(Address(i), i.MemorySize.GetSize()),
            OpKind.Immediate8 or OpKind.Immediate8to64 or OpKind.Immediate32 or OpKind.Immediate32to64 => i.GetImmediate(operand),
            _ => throw new InvalidOperationException("Unmodeled operand")
        };
        private void Destination(Instruction i, ulong value)
        {
            if (i.Op0Kind == OpKind.Register) Set(i.Op0Register, value);
            else if (i.Op0Kind == OpKind.Memory) Write(Address(i), value, i.MemorySize.GetSize());
            else throw new InvalidOperationException("Unmodeled destination");
        }
        private void Push(ulong value) { Set(Register.RSP, Get(Register.RSP) - 8); Write(Get(Register.RSP), value, 8); }
        private ulong Pop() { var value = Read(Get(Register.RSP), 8); Set(Register.RSP, Get(Register.RSP) + 8); return value; }

        // Lower calls are designated contracts in synthetic memory. Their bodies,
        // failure effects, locks and caches are deliberately not simulated as proof.
        private void LowerCall(Instruction i)
        {
            if (Get(Register.RSP) % 16 != 0)
                throw new InvalidOperationException("Wrong pre-call stack alignment.");
            Push(i.NextIP);
            if (Get(Register.RSP) % 16 != 8)
                throw new InvalidOperationException("Wrong lower-entry stack alignment.");
            var a = Get(Register.RCX); var b = Get(Register.RDX); var c = Get(Register.R8);
            ulong returned;
            if (i.NearBranchTarget == _recipe.Find)
            {
                Calls.Add("find");
                if (a != _recipe.MethodTableMap || Read(c, 4) != 0) throw new InvalidOperationException("Wrong lookup ABI.");
                var pointer = Read(c + 8, 8);
                var key = new Key(Read(pointer, 8), Read(pointer + 8, 8), Read(pointer + 16, 8));
                Lookups.Add(key);
                var present = _map.TryGetValue(key, out var value);
                var node = present ? Nodes - 0x100 + value * 24 : _end;
                Write(b, 0xabcdef, 8); Write(b + 8, node, 8);
                if (present) { Write(node + 16, 0x40000 + value * 8, 8); Write(0x40000 + value * 8, value, 8); }
                returned = b;
            }
            else if (i.NearBranchTarget == _recipe.SharedInstantiation)
            {
                Calls.Add("shared"); SharedArguments.Add(a);
                returned = a == _class ? _sharedClass : a == _method ? _sharedMethod :
                    throw new InvalidOperationException("Wrong original shared context.");
            }
            else if (i.NearBranchTarget == _recipe.FullySharedInstantiation)
            {
                Calls.Add("fully-shared"); FullySharedArguments.Add((a, b));
                returned = a == ClassContainer && b == _class ? _fullClass :
                    a == MethodContainer && b == _method ? _fullMethod : a == 0 && b == 0 ? 0ul :
                    throw new InvalidOperationException("Wrong generic container or original context.");
            }
            else if (i.NearBranchTarget == _recipe.MakePointers)
            {
                Calls.Add("make");
                if (a != Result || (c & 255) > 1) throw new InvalidOperationException("Wrong result ABI.");
                MakeFlag = (c & 255) != 0;
                var value = Read(b, 8);
                Write(a, _makeReturnsZero ? 0 : 0x50000 + value, 8);
                Write(a + 8, _makeReturnsZero ? 0 : 0x51000 + value, 8);
                Write(a + 16, _makeReturnsZero ? 0 : 0x52000 + value, 8);
                Write(a + 24, _makeReturnsZero ? 0 : (MakeFlag.Value ? 1ul : 0), 8);
                returned = a;
            }
            else throw new InvalidOperationException("Unmodeled lower target.");
            // Exercise legal volatile-register and shadow-space clobbers.
            var volatileRegisters = new[] { Register.RAX, Register.RCX, Register.RDX, Register.R8, Register.R9, Register.R10, Register.R11 };
            for (var n = 0; n < volatileRegisters.Length; n++) Set(volatileRegisters[n], 0xa0000000 + _seed * 256 + (ulong)n);
            for (ulong n = 0; n < 4; n++) Write(Get(Register.RSP) + 8 + n * 8, 0xb0000000 + _seed + n, 8);
            Set(Register.RAX, returned);
            if (Pop() != i.NextIP) throw new InvalidOperationException("Lower return corrupted.");
        }

        internal void RunAndCheck()
        {
            var pc = 0; var steps = 0;
            while (true)
            {
                if (++steps > 150) throw new InvalidOperationException("Unexpected cycle.");
                var i = _code[pc]; var next = pc + 1;
                switch (i.Mnemonic)
                {
                    case Mnemonic.Mov: Destination(i, Operand(i, 1)); break;
                    case Mnemonic.Lea: Destination(i, Address(i)); break;
                    case Mnemonic.Xor: Destination(i, Operand(i, 0) ^ Operand(i, 1)); break;
                    case Mnemonic.Sub: Destination(i, unchecked(Operand(i, 0) - Operand(i, 1))); break;
                    case Mnemonic.Cmp: _zero = Operand(i, 0) == Operand(i, 1); break;
                    case Mnemonic.Push: Push(Operand(i, 0)); break;
                    case Mnemonic.Pop: Set(i.Op0Register, Pop()); break;
                    case Mnemonic.Je: if (_zero) next = _instructions[i.NearBranchTarget]; break;
                    case Mnemonic.Jne: if (!_zero) next = _instructions[i.NearBranchTarget]; break;
                    case Mnemonic.Jmp: next = _instructions[i.NearBranchTarget]; break;
                    case Mnemonic.Call: LowerCall(i); break;
                    case Mnemonic.Ret: Verify(); return;
                    default: throw new InvalidOperationException("Unmodeled instruction: " + i.Mnemonic);
                }
                pc = next;
            }
        }

        private void Verify()
        {
            var direct = new Key(Definition, _class, _method);
            var shared = new Key(Definition, _sharedClass, _sharedMethod);
            var fully = new Key(Definition, _fullClass, _fullMethod);
            ulong value;
            var expectedLookups = new List<Key> { direct };
            var stage = 0;
            if (!_map.TryGetValue(direct, out value))
            {
                expectedLookups.Add(shared); stage = 1;
                if (!_map.TryGetValue(shared, out value))
                {
                    expectedLookups.Add(fully); stage = 2;
                    if (!_map.TryGetValue(fully, out value)) stage = 3;
                }
            }
            var expectedCalls = new List<string> { "find" };
            if (stage > 0) expectedCalls.AddRange(["shared", "shared", "find"]);
            if (stage > 1) expectedCalls.AddRange(["fully-shared", "fully-shared", "find"]);
            if (stage < 3) expectedCalls.Add("make");
            if (!Calls.SequenceEqual(expectedCalls) || !Lookups.SequenceEqual(expectedLookups)) throw new InvalidOperationException("Selection order or key changed.");
            if (stage > 0 && !SharedArguments.SequenceEqual(new[] { _class, _method })) throw new InvalidOperationException("Shared contexts changed.");
            if (stage > 1 && !FullySharedArguments.SequenceEqual(new[] { (_class == 0 ? 0 : ClassContainer, _class), (_method == 0 ? 0 : MethodContainer, _method) }))
                throw new InvalidOperationException("Fully shared generic-container order changed.");
            if (MakeFlag != (stage < 3 ? stage == 2 : (bool?)null)) throw new InvalidOperationException("Fully shared flag changed.");
            var zero = stage == 3 || _makeReturnsZero;
            if (Read(Result, 8) != (zero ? 0 : 0x50000 + value) || Read(Result + 8, 8) != (zero ? 0 : 0x51000 + value) ||
                Read(Result + 16, 8) != (zero ? 0 : 0x52000 + value) || Read(Result + 24, 8) != (zero ? 0 : stage == 2 ? 1ul : 0))
                throw new InvalidOperationException("Native result changed.");
            if (Get(Register.RAX) != Result || Get(Register.RSP) != 0x100008 || Read(0x100008, 8) != 0xf0000 ||
                Get(Register.RBP) != 0xaa000 || Get(Register.RBX) != 0xbb000 || Get(Register.RSI) != 0xcc000 ||
                Get(Register.RDI) != 0xdd000 || Get(Register.R14) != 0xee000) throw new InvalidOperationException("ABI restoration changed.");
        }
    }

    internal sealed record Result(int InterpretedCases, int ShapeAndFreshnessControls, bool CompleteHelperQualified);

    internal static Result Run(ulong entry, byte[] body)
    {
        var proof = X64GenericMethodSelectionRecipe.Evidence.Identify(entry, body) ?? throw new InvalidOperationException("Original recipe rejected.");
        var interpreted = 0;
        for (var contexts = 0; contexts < 4; contexts++)
        for (var registrations = 0; registrations < 8; registrations++)
        for (var seed = 0; seed < 3; seed++)
        foreach (var capacity in new[] { 3ul, 7ul })
        {
            new Machine(entry, body, proof, contexts, registrations, seed, capacity).RunAndCheck(); interpreted++;
        }
        for (var contexts = 0; contexts < 4; contexts++)
        for (var seed = 0; seed < 3; seed++)
        {
            new Machine(entry, body, proof, contexts, 0, seed, 0).RunAndCheck(); interpreted++;
        }
        foreach (var registrations in new[] { 1, 2, 4 })
        foreach (var capacity in new[] { 3ul, 7ul })
        {
            new Machine(entry, body, proof, 3, registrations, 2, capacity, true).RunAndCheck(); interpreted++;
        }

        var controls = new List<object>();
        void Reject(string name, int ordinal, Func<Instruction, Instruction> mutate)
        {
            var changed = Replace(body, entry, ordinal, mutate);
            var rejected = X64GenericMethodSelectionRecipe.Evidence.Identify(entry, changed) is null;
            var staleRejected = !proof.MatchesBody(changed);
            var restored = proof.MatchesBody(body) && X64GenericMethodSelectionRecipe.Evidence.Identify(entry, body) is not null;
            controls.Add(new { name, rejected, staleRejected, restored });
            if (!rejected || !staleRejected || !restored) throw new InvalidOperationException("Mutation admitted: " + name);
        }
        Reject("direct-hit-branch-inverted", 29, i => { i.Code = Code.Jne_rel8_64; return i; });
        Reject("shared-hit-branch-inverted", 54, i => { i.Code = Code.Je_rel8_64; return i; });
        Reject("fully-shared-miss-branch-inverted", 77, i => { i.Code = Code.Jne_rel8_64; return i; });
        Reject("direct-hit-fully-shared-flag", 31, i => { i.Op0Register = Register.R9D; i.Op1Register = Register.R9D; return i; });
        Reject("fully-shared-result-flag-cleared", 79, i => { i.Immediate8 = 0; return i; });
        Reject("original-method-definition-substituted", 16, i => { i.Op1Register = Register.RCX; return i; });
        Reject("shared-method-reuses-class-context", 37, i => { i.MemoryDisplacement64 = 16; return i; });
        Reject("fully-shared-method-wrong-container", 60, i => { i.MemoryDisplacement64 = 72; return i; });
        Reject("fully-shared-class-wrong-container", 57, i => { i.MemoryDisplacement64 = 248; return i; });
        Reject("second-find-target-diverges", 47, i => { i.NearBranch64 += 16; return i; });
        Reject("second-shared-target-diverges", 39, i => { i.NearBranch64 += 16; return i; });
        Reject("second-fully-shared-target-diverges", 62, i => { i.NearBranch64 += 16; return i; });
        Reject("second-result-builder-target-diverges", 81, i => { i.NearBranch64 += 16; return i; });
        Reject("shared-find-map-diverges", 46, i => { i.MemoryDisplacement64 += 16; return i; });
        Reject("direct-end-capacity-field-diverges", 23, i => { i.MemoryDisplacement64 += 8; return i; });
        Reject("fully-shared-end-entry-base-diverges", 74, i => { i.MemoryDisplacement64 += 8; return i; });
        Reject("missing-result-third-word-wrong", 86, i => { i.Op1Register = Register.RSI; return i; });
        Reject("result-buffer-return-wrong", 89, i => { i.Op1Register = Register.RDI; return i; });
        Reject("nonvolatile-restore-wrong", 90, i => { i.MemoryDisplacement64 = 24; return i; });
        Reject("key-wrapper-points-at-context-only", 18, i => { i.Op1Register = Register.RDX; return i; });
        Reject("result-index-payload-offset-wrong", 30, i => { i.MemoryDisplacement64 = 24; return i; });

        var moved = (byte[])body.Clone();
        foreach (var ordinal in new[] { 22, 47, 70 }) moved = Replace(moved, entry, ordinal, i => { i.NearBranch64 += 16; return i; });
        var newFind = X64GenericMethodSelectionRecipe.Evidence.Identify(entry, moved);
        if (newFind is null || newFind.Find != proof.Find + 16 || proof.MatchesBody(moved) || newFind.CompleteHelperQualified)
            throw new InvalidOperationException("Changed lower obligation was not preserved.");
        controls.Add(new { name = "coherent-changed-find-retains-new-unqualified-obligation", passed = true });
        var copied = (byte[])body.Clone();
        var isolated = X64GenericMethodSelectionRecipe.Evidence.Identify(entry, copied)!;
        copied[0] ^= 1;
        if (!isolated.MatchesBody(body) || isolated.MatchesBody(copied)) throw new InvalidOperationException("Caller array alias changed evidence.");
        controls.Add(new { name = "caller-array-copy-isolated", passed = true });
        if (X64GenericMethodSelectionRecipe.Evidence.Identify(0, body) is not null ||
            X64GenericMethodSelectionRecipe.Evidence.Identify(ulong.MaxValue - 100, body) is not null ||
            X64GenericMethodSelectionRecipe.Evidence.Identify(entry, body[..^1]) is not null ||
            X64GenericMethodSelectionRecipe.Evidence.Identify(entry, [.. body, 0x90]) is not null)
            throw new InvalidOperationException("Invalid boundary admitted.");
        controls.Add(new { name = "zero-overflow-truncation-and-trailing-boundaries-rejected", passed = true });
        foreach (var misaligned in new[] { 0x100000ul, 0x100004ul })
        {
            var rejected = false;
            try { new Machine(entry, body, proof, 3, 1, 0, 3, entryStackPointer: misaligned).RunAndCheck(); }
            catch (InvalidOperationException failure) when (failure.Message == "Wrong pre-call stack alignment.")
            { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Misaligned lower call was admitted.");
        }
        controls.Add(new { name = "misaligned-entry-rejected-before-lower-call", passed = true });
        return new Result(interpreted, controls.Count, proof.CompleteHelperQualified);
    }
}
