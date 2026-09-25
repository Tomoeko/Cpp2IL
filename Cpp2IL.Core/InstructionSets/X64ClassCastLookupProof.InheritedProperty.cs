using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils;
using Iced.Intel;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.InstructionSets;

internal static partial class X64ClassCastLookupProof
{
    // A Release build can inline an inherited auto-property getter into a
    // derived class-cast method. Call that getter in managed IL only when its
    // complete native body is the same field read and it is source-accessible.
    private static bool TryBindInheritedPropertyGetter(TypeAnalysisContext owner,
        int offset, PE pe, X64UnwindProof.Index unwind,
        out FieldAnalysisContext field, out MethodAnalysisContext getter)
    {
        field = null!;
        getter = null!;
        if (offset < 16 || owner.BaseType == null)
            return false;
        var matches = new List<FieldAnalysisContext>();
        var seen = new HashSet<TypeAnalysisContext>();
        for (var type = owner.BaseType; type != null &&
             !ReferenceEquals(type, owner.AppContext.SystemTypes.SystemObjectType);
             type = type.BaseType)
        {
            if (!seen.Add(type) || !PublicOrdinaryClass(type))
                return false;
            matches.AddRange(type.Fields.Where(candidate => !candidate.IsStatic &&
                candidate.Offset == offset));
        }
        if (matches is not [{ } inherited] ||
            inherited.Visibility != FieldAttributes.Private ||
            inherited.DeclaringType.Definition is not { HasCctor: false } ancestorDefinition ||
            inherited.DeclaringType.Methods.Any(method => method.Name == ".cctor") ||
            !X64LiteralConcatProof.UniqueFieldAcrossChain(owner, inherited,
                (ulong)offset))
            return false;

        var properties = inherited.DeclaringType.Properties.Where(property =>
            property.Getter != null && property.Name == property.DefaultName &&
            property.Attributes == property.DefaultAttributes &&
            property.OverridePropertyType == null &&
            property.Definition?.RawPropertyType is
                { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } &&
            ReferenceEquals(property.DefaultPropertyType,
                inherited.FieldType)).ToArray();
        if (properties is not [{ } matchingProperty] ||
            matchingProperty.Getter is not { } candidate ||
            candidate.DeclaringType != inherited.DeclaringType ||
            candidate.Definition is not { GenericContainer: null, parameterCount: 0,
                RawReturnType: { Type: Il2CppTypeEnum.IL2CPP_TYPE_CLASS,
                    NumMods: 0, Byref: 0, Pinned: 0 } } definition ||
            !ReferenceEquals(definition.DeclaringType, ancestorDefinition) ||
            (definition.InternalParameterData?.Length ?? 0) != 0 ||
            candidate.Name != candidate.DefaultName ||
            candidate.Name is ".ctor" or ".cctor" ||
            candidate.Visibility is not (MethodAttributes.Public or
                MethodAttributes.Family or MethodAttributes.FamORAssem) ||
            candidate.IsStatic || candidate.IsVirtual && !candidate.IsFinal ||
            candidate.IsVoid ||
            candidate.Parameters.Count != 0 ||
            candidate.GenericParameters.Count != 0 ||
            candidate.OverrideReturnType != null ||
            !ReferenceEquals(candidate.ReturnType, inherited.FieldType) ||
            candidate.Attributes != candidate.DefaultAttributes ||
            candidate.ImplAttributes != candidate.DefaultImplAttributes ||
            (candidate.Attributes & (MethodAttributes.Abstract |
                                     MethodAttributes.PinvokeImpl)) != 0 ||
            (candidate.ImplAttributes & (MethodImplAttributes.CodeTypeMask |
                                         MethodImplAttributes.ManagedMask |
                                         MethodImplAttributes.InternalCall)) != 0 ||
            RuntimeNullGuardCoalescer.HasOutputOptions(candidate) ||
            !RuntimeNullGuardCoalescer.HasUnchangedNativeSignature(candidate,
                requireUniqueBinding: false) ||
            !X64LiteralConcatProof.HasUnhiddenBaseMethod(owner,
                inherited.DeclaringType, candidate.Name) ||
            !X64LiteralConcatProof.HasUnhiddenBaseMethod(owner,
                inherited.DeclaringType, matchingProperty.Name) ||
            candidate.UnderlyingPointer == 0 ||
            !owner.AppContext.MethodsByAddress.TryGetValue(
                candidate.UnderlyingPointer, out var bindings) ||
            bindings.Where(method => ReferenceEquals(method.DeclaringType,
                inherited.DeclaringType)).ToArray() is not [var bound] ||
            !ReferenceEquals(bound, candidate) ||
            !ProveDirectGetterBody(candidate, offset, pe, unwind))
            return false;

        field = inherited;
        getter = candidate;
        return true;
    }

    private static bool ProveDirectGetterBody(MethodAnalysisContext getter,
        int offset, PE pe, X64UnwindProof.Index unwind)
    {
        var start = getter.UnderlyingPointer;
        if (start > ulong.MaxValue - 5 ||
            unwind.ClassifySpan(start, start + 5).Kind !=
                X64UnwindProof.SpanKind.NoEntry)
            return false;
        getter.EnsureRawBytes();
        var raw = getter.RawBytes;
        if (raw.Length is < 5 or > 20 ||
            !X64AncestorConstructorThunkProof.FileBackedExecutable(pe, unwind,
                raw.AsSpan(), start) ||
            Enumerable.Range(1, raw.Length - 1).Any(index =>
                getter.AppContext.MethodsByAddress.ContainsKey(start + (ulong)index)))
            return false;

        var native = X86Utils.Iterate(getter).ToArray();
        return native.Length is >= 2 and <= 16 && native[0].IP == start &&
               native.Take(2).All(instruction => !instruction.IsInvalid &&
                   instruction.CodeSize == CodeSize.Code64 &&
                   !instruction.HasLockPrefix && !instruction.HasRepPrefix &&
                   !instruction.HasRepnePrefix &&
                   instruction.SegmentPrefix == Register.None) &&
               native[0].Code == Code.Mov_r64_rm64 &&
               native[0].Op0Kind == OpKind.Register &&
               native[0].Op0Register == Register.RAX &&
               native[0].Op1Kind == OpKind.Memory &&
               native[0].MemoryBase == Register.RCX &&
               native[0].MemoryIndex == Register.None &&
               native[0].MemorySize.GetSize() == 8 &&
               native[0].MemoryDisplacement64 == (ulong)offset &&
               native[1].Code == Code.Retnq && native[1].OpCount == 0 &&
               native[1].IP == native[0].NextIP &&
               native[1].NextIP == start + 5 &&
               native.Skip(2).All(instruction => instruction.Code == Code.Int3) &&
               X64NativePaddingProof.HasInt3Padding(pe, native[1].NextIP,
                   start + (ulong)raw.Length) &&
               X86CallerExceptionRegionProof.Check(getter, native.Take(2).ToArray(),
                   new HashSet<ulong>()) == null;
    }
}
