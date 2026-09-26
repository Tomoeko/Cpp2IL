using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Utils;

public abstract class BaseCallingConventionResolver
{
    public static bool IsFloatingPoint(TypeAnalysisContext type)
        => type == type.AppContext.SystemTypes.SystemSingleType || type == type.AppContext.SystemTypes.SystemDoubleType;

    protected static bool IsFloatingPoint(ParameterAnalysisContext par) => IsFloatingPoint(par.ParameterType);

    public abstract Register ReturnRegister(MethodAnalysisContext ctx);

    public abstract bool ReturnsViaHiddenBuffer(MethodAnalysisContext ctx);

    public abstract Register? HiddenReturnBufferRegister(MethodAnalysisContext ctx);

    public abstract IOperand[] ResolveForManaged(MethodAnalysisContext ctx);

    protected abstract (string[] Integer, string[] Float) RawRegisters(ApplicationAnalysisContext app);

    // MSVC style: argument slot n is integer register n or float register n, not both
    protected virtual bool UsesShadowedArgumentSlots(ApplicationAnalysisContext app) => false;

    // false when the return buffer pointer lives outside the argument registers (e.g. arm64 uses x8)
    protected virtual bool HiddenBufferConsumesArgumentSlot => true;

    public IOperand[] ResolveForUnmanaged(ApplicationAnalysisContext app, ulong target)
    {
        // We don't know the callee's signature, so preserve every argument register.

        var (integerRegisters, floatRegisters) = RawRegisters(app);
        return integerRegisters.Concat(floatRegisters).Select(name => (IOperand)new Register(null, name)).ToArray();
    }

    public bool HasRawArgumentLayout(Instruction call, ApplicationAnalysisContext app)
    {
        var (integerRegisters, floatRegisters) = RawRegisters(app);
        var argBase = ArgBase(call);

        if (call.RawCallStackArgumentCount >= 0)
            return UsesShadowedArgumentSlots(app) && call.Operands.Count ==
                argBase + integerRegisters.Length + floatRegisters.Length + call.RawCallStackArgumentCount;

        if (call.Operands.Count != argBase + integerRegisters.Length + floatRegisters.Length)
            return false;

        for (var i = 0; i < integerRegisters.Length; i++)
            if (RegisterName(call.Operands[argBase + i]) != integerRegisters[i])
                return false;

        for (var i = 0; i < floatRegisters.Length; i++)
            if (RegisterName(call.Operands[argBase + integerRegisters.Length + i]) != floatRegisters[i])
                return false;

        return true;
    }

    public void RemapRawArguments(Instruction call, MethodAnalysisContext resolved)
    {
        var app = resolved.AppContext;

        if (!HasRawArgumentLayout(call, app))
            return;

        var (integerRegisters, floatRegisters) = RawRegisters(app);
        var argBase = ArgBase(call);

        var slots = new List<(bool IsFloat, bool Emit)>();
        if (ReturnsViaHiddenBuffer(resolved) && HiddenBufferConsumesArgumentSlot)
            slots.Add((false, false));
        if (!resolved.IsStatic)
            slots.Add((false, true));
        foreach (var parameter in resolved.Parameters)
            slots.Add((IsFloatingPoint(parameter), true));
        slots.Add((false, true)); // the MethodInfo argument

        var operands = new List<IOperand>(argBase + slots.Count);
        for (var i = 0; i < argBase; i++)
            operands.Add(call.Operands[i]);

        if (UsesShadowedArgumentSlots(app))
        {
            for (var slot = 0; slot < slots.Count; slot++)
                if (slots[slot].Emit)
                {
                    var offset = slot < integerRegisters.Length
                        ? (slots[slot].IsFloat ? integerRegisters.Length + slot : slot)
                        : floatRegisters.Length + slot;
                    if (argBase + offset >= call.Operands.Count)
                        break; // Missing stack evidence remains a missing argument.
                    operands.Add(call.Operands[argBase + offset]);
                }
        }
        else
        {
            // independent integer/float counters
            var (integer, floating) = (0, 0);

            foreach (var (isFloat, emit) in slots)
            {
                if (isFloat ? floating >= floatRegisters.Length : integer >= integerRegisters.Length)
                    break;

                var operand = call.Operands[argBase + (isFloat ? integerRegisters.Length + floating++ : integer++)];
                if (emit)
                    operands.Add(operand);
            }
        }

        call.SetOperands(operands);
        call.RawCallStackArgumentCount = -1;
        ResolveDeferredReturn(call, resolved);
    }

    internal IOperand? HiddenMethodInfoArgument(Instruction call, MethodAnalysisContext method)
    {
        var argBase = ArgBase(call);
        var offset = (method.IsStatic ? 0 : 1) + method.Parameters.Count;
        if (HasRawArgumentLayout(call, method.AppContext))
        {
            if (ReturnsViaHiddenBuffer(method) && HiddenBufferConsumesArgumentSlot)
                offset++;
            var (integerRegisters, floatRegisters) = RawRegisters(method.AppContext);
            if (!UsesShadowedArgumentSlots(method.AppContext))
                offset -= method.Parameters.Count(IsFloatingPoint);
            else if (offset >= integerRegisters.Length)
                offset += floatRegisters.Length;
        }
        return argBase + offset < call.Operands.Count ? call.Operands[argBase + offset] : null;
    }

    private void ResolveDeferredReturn(Instruction call, MethodAnalysisContext resolved)
    {
        if (call.DeferredCallReturns is not { } captures)
            return;
        call.DeferredCallReturns = null;
        if (resolved.IsVoid)
            return;
        // Identifying the callee does not transfer its buffer result into the
        // caller's managed storage. Dropping that result can fabricate default
        // field values when later native instructions read the supplied buffer.
        if (ReturnsViaHiddenBuffer(resolved))
        {
            call.OpCode = OpCode.NotImplemented;
            call.SetOperands(new StringLiteral("Shared native call return buffer requires proved managed storage and result transfer"));
            return;
        }
        var register = ReturnRegister(resolved).Name;
        var capture = captures.SingleOrDefault(instruction => instruction is
            { OpCode: OpCode.UnresolvedValue, Operands: [LocalVariable result, ..] }
            && result.Register.Name == register);
        if (capture == null)
            return; // An unused native return was already removed as dead code.
        var operands = call.Operands.ToList();
        operands.Insert(1, capture.Operands[0]);
        call.OpCode = OpCode.Call;
        call.SetOperands(operands);
        capture.OpCode = OpCode.Nop;
        capture.SetOperands();
    }

    protected static int ArgBase(Instruction call) => call.OpCode is OpCode.CallVoid ? 1 : 2;

    private static string? RegisterName(IOperand operand) => operand switch
    {
        Register register => register.Name,
        LocalVariable { Register.Name: var name } => name,
        _ => null
    };
}
