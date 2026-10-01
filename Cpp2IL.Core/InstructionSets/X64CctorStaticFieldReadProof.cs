using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

/// <summary>
/// Binds a complete TypeInfo-guarded reference field read with an explicit
/// class-initialization call. The caller recipe is proved; the native class
/// initializer's failure, concurrency and cleanup effects remain unresolved.
/// </summary>
internal static class X64CctorStaticFieldReadProof
{
    internal sealed record Shape(ulong OnceFlag, ulong TypeInfoSlot,
        ulong MetadataInitializer, ulong ClassInitializer, int FieldOffset);

    internal sealed record Evidence(FieldAnalysisContext Field, MethodAnalysisContext Constructor,
        Shape Native, X64SmallAggregateFieldGetterProof.InputState Input)
    {
        internal bool Matches(Evidence other) => Native == other.Native &&
            ReferenceEquals(Field, other.Field) && ReferenceEquals(Constructor, other.Constructor) &&
            Input.Matches(other.Input);
    }

    internal static Evidence? Find(MethodAnalysisContext method)
    {
        try
        {
            var app = method.AppContext;
            if (!X86RuntimeNullThrowProof.IsSupportedProfile(app) || app.Binary is not PE pe ||
                X64UnwindProof.ForApplication(app) is not { } unwind ||
                X64Stack28BodyProof.Read(method, 16, 128) is not { } body ||
                TryProveShape(body) is not { } shape ||
                X86CallerExceptionRegionProof.Check(method, body, new HashSet<ulong>()) != null ||
                !X64PeOnceFlagProof.IsInitiallyZero(pe, unwind, shape.OnceFlag) ||
                !X64MetadataStaticGetterProof.FileBackedWritableData(pe, unwind, shape.TypeInfoSlot, 8) ||
                !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, shape.TypeInfoSlot, 8) ||
                shape.OnceFlag >= shape.TypeInfoSlot && shape.OnceFlag - shape.TypeInfoSlot < 8 ||
                !X64MetadataInitializationHelperProof.TryIdentifyTypeInfo(app, pe, unwind,
                    shape.MetadataInitializer) ||
                ClassInitializerBytes(app, pe, unwind, shape.ClassInitializer) is not { } initializerBytes ||
                app.LibCpp2IlContext.GetRawTypeGlobalByAddress(shape.TypeInfoSlot) is not
                    { Type: MetadataUsageType.TypeInfo, IsValid: true } usage ||
                X64OriginalReferenceClassProof.ResolveClass(app, usage.AsType()) is not { } owner ||
                !app.MethodsByAddress.TryGetValue(method.UnderlyingPointer, out var bindings) ||
                bindings.Count is < 1 or > 128 || bindings.Distinct().Count() != bindings.Count ||
                bindings.Count(candidate => ReferenceEquals(candidate, method)) != 1)
                return null;

            var rawSlot = pe.ReadPointerAtVirtualAddress(shape.TypeInfoSlot);
            if (owner.Definition is not { } type || rawSlot !=
                ((ulong)MetadataUsageType.TypeInfo << 29 | (ulong)type.ByvalTypeIndex.Value << 1 | 1))
                return null;

            var values = new List<object> { app, app.Metadata, pe, rawSlot, bindings.Count };
            FieldAnalysisContext? selectedField = null;
            MethodAnalysisContext? selectedConstructor = null;
            foreach (var binding in bindings)
            {
                // Folded identities are checked before eligibility. No original
                // alias may be omitted merely because its declaration differs.
                if (binding.UnderlyingPointer != method.UnderlyingPointer ||
                    !Bind(binding, owner, shape, out var field, out var constructor, out var layout))
                    return null;
                if (ReferenceEquals(binding, method))
                {
                    selectedField = field;
                    selectedConstructor = constructor;
                }
                values.AddRange([binding.Definition!, owner.Definition!, field.BackingData!.Field,
                    constructor.Definition!, field, constructor, layout.Length]);
                foreach (var item in layout) values.Add(item);
                X64SmallAggregateFieldGetterProof.CaptureMethod(binding, values);
                X64SmallAggregateFieldGetterProof.CaptureRawType(binding.Definition!.RawReturnType!, values);
                X64SmallAggregateFieldGetterProof.CaptureType(owner, values);
                X64SmallAggregateFieldGetterProof.CaptureMethod(constructor, values);
                values.AddRange([owner.Methods.Count, field.FieldType, field.FieldType.Definition!,
                    field.FieldType.Name, field.FieldType.Namespace, field.FieldType.Attributes]);
                foreach (var member in owner.Methods) values.Add(member);
                foreach (var member in owner.Fields) values.Add(member.BackingData!.Field);
            }
            if (selectedField == null || selectedConstructor == null) return null;
            var length = checked((int)(body[15].NextIP - method.UnderlyingPointer));
            var bytes = method.RawBytes.AsSpan().Slice(0, length).ToArray().Concat(initializerBytes).ToArray();
            return new Evidence(selectedField, selectedConstructor, shape,
                new X64SmallAggregateFieldGetterProof.InputState(values, bytes));
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or OverflowException or KeyNotFoundException or NullReferenceException)
        {
            return null;
        }
    }

    internal static Shape? TryProveShape(IReadOnlyList<Instruction> body)
    {
        if (body.Count != 16 || body.Any(instruction => instruction.IsInvalid ||
                instruction.CodeSize != CodeSize.Code64 || instruction.HasLockPrefix || instruction.HasRepPrefix ||
                instruction.HasRepnePrefix || instruction.SegmentPrefix != Register.None) ||
            body.Where((instruction, index) => index > 0 && instruction.IP != body[index - 1].NextIP).Any() ||
            body[0].Code != Code.Sub_rm64_imm8 || !X64Stack28BodyProof.Stack(body[0], Mnemonic.Sub) ||
            body[1].Code != Code.Cmp_rm8_imm8 || !Memory(body[1], 0, Register.RIP, 1) ||
            body[1].Op1Kind != OpKind.Immediate8 || body[1].Immediate8 != 0 ||
            !Branch(body[2], body[6].IP) ||
            body[3].Code != Code.Lea_r64_m || body[3].Op0Kind != OpKind.Register || body[3].Op0Register != Register.RCX ||
            !Memory(body[3], 1, Register.RIP, 0) || !Call(body[4]) ||
            body[5].Code != Code.Mov_rm8_imm8 || !Memory(body[5], 0, Register.RIP, 1) ||
            body[5].Op1Kind != OpKind.Immediate8 || body[5].Immediate8 != 1 ||
            body[5].IPRelativeMemoryAddress != body[1].IPRelativeMemoryAddress ||
            !RipLoad(body[6]) || body[6].IPRelativeMemoryAddress != body[3].IPRelativeMemoryAddress ||
            body[7].Code != Code.Cmp_rm32_imm8 || !Memory(body[7], 0, Register.RAX, 4) ||
            body[7].MemoryDisplacement64 != (ulong)Il2CppClassLayout.CctorFinishedOrNoCctorOffset64 ||
            body[7].Op1Kind != OpKind.Immediate8to32 || body[7].Immediate8 != 0 ||
            !Branch(body[8], body[12].IP) ||
            body[9].Code is not (Code.Mov_r64_rm64 or Code.Mov_rm64_r64) || body[9].OpCount != 2 ||
            body[9].Op0Kind != OpKind.Register || body[9].Op0Register != Register.RCX ||
            body[9].Op1Kind != OpKind.Register || body[9].Op1Register != Register.RAX ||
            !Call(body[10]) || !RipLoad(body[11]) || body[11].IPRelativeMemoryAddress != body[3].IPRelativeMemoryAddress ||
            !X64MetadataStaticGetterProof.FieldLoadPair(body[12], body[13], out var offset, out var width) ||
            width != 8 || offset > int.MaxValue ||
            body[14].Code != Code.Add_rm64_imm8 || !X64Stack28BodyProof.Stack(body[14], Mnemonic.Add) ||
            body[15].Code != Code.Retnq || body[15].OpCount != 0 ||
            body[1].IPRelativeMemoryAddress == body[3].IPRelativeMemoryAddress)
            return null;
        return new Shape(body[1].IPRelativeMemoryAddress, body[3].IPRelativeMemoryAddress,
            body[4].NearBranchTarget, body[10].NearBranchTarget, (int)offset);
    }

    private static bool Bind(MethodAnalysisContext method, TypeAnalysisContext owner, Shape shape,
        out FieldAnalysisContext field, out MethodAnalysisContext constructor, out byte[] layout)
    {
        field = null!;
        constructor = null!;
        layout = [];
        var app = method.AppContext;
        if (!ReferenceEquals(method.DeclaringType, owner) || owner.Definition is not
                { HasCctor: true, GenericContainer: null, PackingSizeIsDefault: true, ClassSizeIsDefault: true } type ||
            owner.IsValueType || owner.IsInterface || owner.IsGenericInstance || owner.GenericParameters.Count != 0 ||
            owner.DeclaringType != null || owner.Name != owner.DefaultName || owner.Namespace != owner.DefaultNamespace ||
            owner.Attributes != owner.DefaultAttributes || owner.OverrideBaseType != null ||
            (owner.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout ||
            !X64OriginalReferenceClassProof.OriginalType(app, type) ||
            !X64OriginalReferenceClassProof.CanonicalAssembly(owner.DeclaringAssembly) ||
            !ReferenceEquals(X64OriginalReferenceClassProof.ResolveClass(app, type.RawType), owner) ||
            method.Definition is not { GenericContainer: null, parameterCount: 0 } definition ||
            !X64OriginalReferenceClassProof.OriginalMethod(app, definition) ||
            !X64OriginalReferenceClassProof.OriginalMethodPointer(method) ||
            !ReferenceEquals(definition.DeclaringType, type) || (definition.InternalParameterData?.Length ?? 0) != 0 ||
            !X64OriginalReferenceClassProof.ValidTypeIndex(app, definition.returnTypeIdx.Value) ||
            definition.RawReturnType is not { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS, NumMods: 0, Byref: 0, Pinned: 0, ValueType: 0 } rawReturn ||
            !X64OriginalReferenceClassProof.RetainedDescriptor(app, rawReturn) ||
            !ReferenceEquals(X64OriginalReferenceClassProof.ResolveClass(app, rawReturn), method.ReturnType) ||
            method.ReturnType.Definition is not { } returnDefinition ||
            !X64OriginalReferenceClassProof.OriginalType(app, returnDefinition) ||
            method.ReturnType.Name != method.ReturnType.DefaultName || method.ReturnType.Namespace != method.ReturnType.DefaultNamespace ||
            method.ReturnType.Attributes != method.ReturnType.DefaultAttributes ||
            method.Name is ".ctor" or ".cctor" || method.IsVoid || method.Name != method.DefaultName ||
            method.Parameters.Count != 0 || method.GenericParameters.Count != 0 || method.OverrideReturnType != null ||
            method.Attributes != method.DefaultAttributes || method.ImplAttributes != method.DefaultImplAttributes ||
            (method.Attributes & (MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0 ||
            (method.ImplAttributes & (MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask |
                MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(method) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(method, requireUniqueBinding: false) ||
            !(type.Fields ?? []).SequenceEqual(owner.Fields.Select(member => member.BackingData?.Field)) ||
            !(type.Methods ?? []).SequenceEqual(owner.Methods.Select(member => member.Definition)) ||
            !X64OriginalReferenceClassProof.OriginalInstanceFieldLayout(owner, out layout))
            return false;

        var constructors = owner.Methods.Where(member => member.Name == ".cctor").ToArray();
        if (constructors is not [var cctor] || !X64OriginalReferenceClassProof.StaticConstructor(cctor) ||
            !ReferenceEquals(cctor.DeclaringType, owner) || !X64OriginalReferenceClassProof.OriginalMethodPointer(cctor) ||
            cctor.UnderlyingPointer is 0 or ulong.MaxValue)
            return false;
        var pe = (PE)app.Binary;
        var sizes = pe.ReadReadableAtVirtualAddress<Il2CppTypeDefinitionSizes>(pe.TypeDefinitionSizePointers[type.TypeIndex.Value]);
        if (sizes.instance_size != type.RawSizes.instance_size || sizes.native_size != type.RawSizes.native_size ||
            sizes.static_fields_size != type.RawSizes.static_fields_size || sizes.thread_static_fields_size != type.RawSizes.thread_static_fields_size ||
            (ulong)shape.FieldOffset + 8 > sizes.static_fields_size)
            return false;
        foreach (var member in owner.Fields)
            if (member.BackingData?.Field is not { RawFieldType: { } raw } original ||
                !X64OriginalReferenceClassProof.OriginalField(app, original) || !ReferenceEquals(original.DeclaringType, type) ||
                !ReferenceEquals(member.DeclaringType, owner) || !X64OriginalReferenceClassProof.RetainedDescriptor(app, raw) ||
                member.Name != member.DefaultName || member.Attributes != member.DefaultAttributes ||
                member.Offset != member.DefaultOffset || member.OverrideFieldType != null || member.UseOverrideConstantValue)
                return false;
        var fields = owner.Fields.Where(member => member.IsStatic && member.Offset == shape.FieldOffset &&
            (member.Attributes & FieldAttributes.Literal) == 0).ToArray();
        if (fields is not [var selected] ||
            !X64MetadataStaticGetterProof.UnchangedField(selected, method.ReturnType, rawReturn, 8))
            return false;
        field = selected;
        constructor = cctor;
        return true;
    }

    // This authenticates an exported thunk and its complete native .pdata body,
    // including bodies with handlers. It does not qualify any lower effects.
    private static byte[]? ClassInitializerBytes(ApplicationAnalysisContext app, PE pe,
        X64UnwindProof.Index unwind, ulong export)
    {
        if (export != X64PeExportProof.Find(pe, unwind, "il2cpp_runtime_class_init") ||
            X64NativeInstructionReader.Read(pe, unwind, export, 1, 5) is not [var thunk] ||
            thunk.Code != Code.Jmp_rel32_64 || thunk.OpCount != 1 || thunk.Length != 5 ||
            unwind.ClassifySpan(export, thunk.NextIP).Kind != X64UnwindProof.SpanKind.NoEntry)
            return null;
        var target = thunk.NearBranchTarget;
        var span = unwind.ClassifySpan(target, checked(target + 1));
        if (span.Start != target || span.End <= target || span.End - target > 4096 ||
            X64NativeInstructionReader.HasInteriorManagedEntry(app, target, span.End) ||
            !X64PeOnceFlagProof.IsUnrelocatedRange(pe, unwind, target, checked((uint)(span.End - target))))
            return null;
        var offset = pe.MapVirtualAddressToRaw(target, false);
        var length = checked((int)(span.End - target));
        var image = pe.GetRawBinaryContent();
        if (offset < 0 || offset > image.Length - length) return null;
        var body = image.Slice(checked((int)offset), length);
        if (!X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind, body, target)) return null;
        var thunkOffset = checked((int)pe.MapVirtualAddressToRaw(export, false));
        return image.Slice(thunkOffset, 5).ToArray().Concat(body.ToArray()).ToArray();
    }

    private static bool Memory(Instruction instruction, int operand, Register basis, int width) =>
        instruction.OpCount == 2 && instruction.GetOpKind(operand) == OpKind.Memory &&
        instruction.MemoryBase == basis && instruction.MemoryIndex == Register.None &&
        instruction.MemoryIndexScale == 1 && (width == 0 || instruction.MemorySize.GetSize() == width);

    private static bool RipLoad(Instruction instruction) => instruction.Code == Code.Mov_r64_rm64 &&
        instruction.Op0Kind == OpKind.Register && instruction.Op0Register == Register.RAX && Memory(instruction, 1, Register.RIP, 8);

    private static bool Branch(Instruction instruction, ulong target) =>
        instruction.Code is Code.Jne_rel8_64 or Code.Jne_rel32_64 && instruction.OpCount == 1 &&
        instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget == target;

    private static bool Call(Instruction instruction) => instruction.Code == Code.Call_rel32_64 &&
        instruction.OpCount == 1 && instruction.Op0Kind == OpKind.NearBranch64 && instruction.NearBranchTarget != 0;
}
