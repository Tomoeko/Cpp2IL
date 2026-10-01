using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

// Mutate only the loaded neutral input. Raw files and validation oracles stay intact.
internal static class X64GenericMethodTableControls
{
    internal sealed record Result(string Name, bool ExpectedFresh, bool FreshAccepted,
        bool ExpectedSaved, bool SavedAccepted, bool? ExpectedSelected, bool SelectedAccepted,
        bool Restored, string? Exception);

    internal static Result[] Run(ApplicationAnalysisContext app)
    {
        var pe = (PE)app.Binary;
        var index = X64UnwindProof.ForApplication(app)
            ?? throw new InvalidOperationException("Original unwind index is unavailable.");
        var evidence = X64GenericMethodTableProof.TryIdentify(app, pe, index)
            ?? throw new InvalidOperationException("Clean complete registration was rejected.");
        var definition = app.GetAssemblyByName("NativeDirectGenericReferenceInvocationFixture")!
            .Types.Single(type => type.Name == "Relay").Methods.Single(method => method.Name == "Echo").Definition!;
        var reference = pe.ConcreteGenericMethods[definition].Single(value =>
            value.MethodGenericParams is [{ Type: Il2CppTypeEnum.IL2CPP_TYPE_OBJECT }]);
        if (!pe.TryGetGenericMethodRegistration(reference, out var selected))
            throw new InvalidOperationException("Original selected registration is unavailable.");
        var pointer = reference.GenericVariantPtr;
        bool Selected() => X64OriginalReferenceClassProof.OriginalGenericMethodReference(app, reference, pointer);
        bool Fresh() => X64GenericMethodTableProof.TryIdentify(app, pe, index) != null;
        bool Saved() => evidence.Matches(app, pe, index);
        bool Restored() => Fresh() && Saved() && Selected();

        var raw = (byte[])PrivateField(typeof(PE), "raw").GetValue(pe)!;
        var specRows = app.Metadata.methodSpecs;
        var functionRows = app.Metadata.genericMethodTables;
        var instancesField = PrivateField(typeof(Il2CppBinary), "_genericInsts");
        var pointersField = PrivateField(typeof(Il2CppBinary), "_genericMethodPointers");
        var instances = (Il2CppGenericInst[])instancesField.GetValue(pe)!;
        var pointers = (ulong[])pointersField.GetValue(pe)!;
        var origin = evidence.Origin;
        var unusedSpec = specRows.Length - 1;
        var unusedFunction = functionRows.Length - 1;
        if (unusedSpec == selected.SpecificationIndex || unusedFunction == selected.TableIndex)
            throw new InvalidOperationException("The neutral input has no unselected tail row.");
        var canonical = evidence.Instantiations.Rows[evidence.Instantiations.CanonicalObjectOrdinal];
        var neighbor = evidence.Instantiations.Rows.ToArray().First(row =>
            row.Ordinal != canonical.Ordinal && row.Count == 1);
        var descriptor = evidence.Instantiations.Rows.ToArray().SelectMany(row => row.Descriptors.ToArray())
            .First(value => value.Type == Il2CppTypeEnum.IL2CPP_TYPE_CLASS && value.Dummy > 0);
        var results = new List<Result>();

        void Check(string name, Action<Mutation> mutate, bool expectedFresh = false,
            bool expectedSaved = false, bool? expectedSelected = true, bool interrupt = false)
        {
            if (!Restored()) throw new InvalidOperationException("Missing restoration before " + name);
            var fresh = false;
            var saved = false;
            var selectedAccepted = false;
            var interrupted = false;
            string? exception = null;
            // Register rollback before each edit, so an exception during mutation
            // setup or proof evaluation cannot leave a partially changed context.
            using (var changes = new Mutation(pe, raw))
            {
                try
                {
                    mutate(changes);
                    if (interrupt) throw new ControlledInterruption();
                    fresh = Fresh();
                    saved = Saved();
                    selectedAccepted = Selected();
                }
                catch (ControlledInterruption) when (interrupt)
                {
                    interrupted = true;
                }
                catch (Exception failure)
                {
                    exception = failure.GetType().Name;
                }
            }
            if (interrupt)
            {
                if (!interrupted) throw new InvalidOperationException("The interruption control did not interrupt.");
                // This control observes the real proofs after exception cleanup;
                // it makes no assertion about proof results during interruption.
                fresh = Fresh();
                saved = Saved();
                selectedAccepted = Selected();
            }
            var restored = Restored();
            results.Add(new(name, expectedFresh, fresh, expectedSaved, saved,
                expectedSelected, selectedAccepted, restored, exception));
            if (!restored) throw new InvalidOperationException("Restoration failed: " + name);
        }

        Check("equivalent-metadata-substitution-cannot-borrow-registration", changes =>
        {
            var context = app.LibCpp2IlContext;
            var property = typeof(LibCpp2IlContext).GetProperty(nameof(LibCpp2IlContext.Metadata))!;
            changes.Assign(() => context.Metadata, value => property.SetValue(context, value), Clone(context.Metadata));
        }, expectedSelected: false);
        Check("new-context-cannot-borrow-registration", changes =>
        {
            var context = (LibCpp2IlContext)Activator.CreateInstance(typeof(LibCpp2IlContext),
                BindingFlags.Instance | BindingFlags.NonPublic, null, [app.LibCpp2IlContext.Settings], null)!;
            typeof(LibCpp2IlContext).GetProperty(nameof(LibCpp2IlContext.Binary))!.SetValue(context, pe);
            typeof(LibCpp2IlContext).GetProperty(nameof(LibCpp2IlContext.Metadata))!.SetValue(context, app.Metadata);
            changes.Assign(() => app.LibCpp2IlContext, value => app.LibCpp2IlContext = value, context);
        }, expectedSelected: false);

        Check("coherent-specification-tail-truncation", changes =>
        {
            changes.Write64(origin.MetadataRegistrationAddress + 64, (ulong)specRows.Length - 1);
            changes.Assign(() => app.Metadata.methodSpecs, value => app.Metadata.methodSpecs = value, specRows[..^1]);
        }, expectedSelected: false);
        Check("coherent-function-tail-truncation", changes =>
        {
            changes.Write64(origin.MetadataRegistrationAddress + 32, (ulong)functionRows.Length - 1);
            changes.Assign(() => app.Metadata.genericMethodTables, value => app.Metadata.genericMethodTables = value, functionRows[..^1]);
        }, expectedSelected: false);
        Check("coherent-method-pointer-tail-truncation", changes =>
        {
            changes.Write64(origin.CodeRegistrationAddress + 16, (ulong)pointers.Length - 1);
            changes.Array(pointersField, pointers[..^1]);
        }, expectedSelected: false);
        Check("coherent-instantiation-tail-truncation", changes =>
        {
            changes.Write64(origin.MetadataRegistrationAddress + 16, (ulong)instances.Length - 1);
            changes.Array(instancesField, instances[..^1]);
        }, expectedSelected: false);
        Check("unselected-equivalent-specification-instance", changes =>
            changes.Assign(() => specRows[unusedSpec], value => specRows[unusedSpec] = value, Clone(specRows[unusedSpec])));
        Check("unselected-coherent-specification-definition", changes =>
        {
            var row = specRows[unusedSpec];
            var changed = (row.methodDefinitionIndex.Value + 1) % app.Metadata.MethodDefinitionCount;
            changes.Write32(origin.SpecificationsAddress + (ulong)unusedSpec * 12, changed);
            changes.Assign(() => row.methodDefinitionIndex, value => row.methodDefinitionIndex = value,
                Il2CppVariableWidthIndex<Il2CppMethodDefinition>.MakeTemporaryForFixedWidthUsage(changed));
        });
        Check("unselected-coherent-specification-instantiation", changes =>
        {
            var row = specRows[unusedSpec];
            var changed = row.methodIndexIndex.Value == canonical.Ordinal ? neighbor.Ordinal : canonical.Ordinal;
            changes.Write32(origin.SpecificationsAddress + (ulong)unusedSpec * 12 + 8, changed);
            changes.Assign(() => row.methodIndexIndex, value => row.methodIndexIndex = value,
                Il2CppVariableWidthIndex<Il2CppGenericInst>.MakeTemporaryForFixedWidthUsage(changed));
        });
        Check("unselected-equivalent-function-instance", changes =>
            changes.Assign(() => functionRows[unusedFunction], value => functionRows[unusedFunction] = value,
                Clone(functionRows[unusedFunction])));
        Check("unselected-coherent-function-invoker", changes =>
        {
            var row = functionRows[unusedFunction];
            var changed = row.invokerIndex == 0 ? 1 : 0;
            changes.Write32(origin.FunctionsAddress + (ulong)unusedFunction * 16 + 8, changed);
            changes.Assign(() => row.invokerIndex, value => row.invokerIndex = value, changed);
        });
        Check("unselected-coherent-function-specification", changes =>
        {
            var row = functionRows[unusedFunction];
            var changed = row.GenericMethodIndex == 0 ? 1 : 0;
            changes.Write32(origin.FunctionsAddress + (ulong)unusedFunction * 16, changed);
            changes.Assign(() => row.GenericMethodIndex, value => row.GenericMethodIndex = value, changed);
        });
        Check("unselected-coherent-function-method-pointer", changes =>
        {
            var row = functionRows[unusedFunction];
            var changed = row.methodIndex == 0 ? 1 : 0;
            changes.Write32(origin.FunctionsAddress + (ulong)unusedFunction * 16 + 4, changed);
            changes.Assign(() => row.methodIndex, value => row.methodIndex = value, changed);
        });
        Check("unselected-coherent-function-adjustor", changes =>
        {
            var row = functionRows[unusedFunction];
            var changed = row.adjustorThunk == -1 ? 0 : -1;
            changes.Write32(origin.FunctionsAddress + (ulong)unusedFunction * 16 + 12, changed);
            changes.Assign(() => row.adjustorThunk, value => row.adjustorThunk = value, changed);
        });
        Check("unselected-equivalent-instantiation-instance", changes =>
            changes.Assign(() => instances[neighbor.Ordinal], value => instances[neighbor.Ordinal] = value,
                Clone(instances[neighbor.Ordinal])));
        Check("unselected-native-instantiation-slot", changes =>
            changes.Write64(evidence.Instantiations.Table.Address + (ulong)neighbor.Ordinal * 8, canonical.Address));
        Check("unselected-coherent-instantiation-count", changes =>
        {
            var row = instances[neighbor.Ordinal];
            changes.Write64(neighbor.Address, row.pointerCount + 1);
            changes.Assign(() => row.pointerCount, value => row.pointerCount = value, row.pointerCount + 1);
        });
        Check("unselected-coherent-instantiation-argv", changes =>
        {
            var row = instances[neighbor.Ordinal];
            changes.Write64(neighbor.Address + 8, canonical.Arguments);
            changes.Assign(() => row.pointerStart, value => row.pointerStart = value, canonical.Arguments);
        });
        Check("unselected-cached-descriptor-union-null", changes =>
            changes.Assign(() => descriptor.Original.Data, value => descriptor.Original.Data = value, null!),
            expectedSelected: null);
        Check("unselected-cached-descriptor-flags", changes =>
            changes.Assign(() => descriptor.Original.Pinned, value => descriptor.Original.Pinned = value,
                descriptor.Original.Pinned ^ 1), expectedSelected: null);
        Check("unselected-coherent-class-descriptor-wide-union", changes =>
            changes.DescriptorUnion(descriptor, descriptor.Datapoint + (1ul << 32)), expectedSelected: null);
        // Initial descriptor values have no producer snapshot. A coherent bounded
        // new value can be captured, but must invalidate the earlier evidence.
        Check("unselected-coherent-class-descriptor-bounded-change", changes =>
            changes.DescriptorUnion(descriptor, (descriptor.Datapoint + 1) % (ulong)app.Metadata.TypeDefinitionCount),
            expectedFresh: true, expectedSelected: null);
        Check("raw-method-pointer-count-wide", changes =>
            changes.Write64(origin.CodeRegistrationAddress + 16, origin.MethodPointerCount + (1ul << 32)), expectedSelected: false);
        Check("raw-invoker-count-lowered", changes =>
            changes.Write64(origin.CodeRegistrationAddress + 40, origin.InvokerCount - 1), expectedSelected: false);
        Check("raw-adjustor-base-changed", changes =>
            changes.Write64(origin.CodeRegistrationAddress + 32, origin.AdjustorThunksAddress + 8), expectedSelected: false);
        Check("unselected-native-method-pointer-changed", changes =>
            changes.Write64(origin.MethodPointersAddress + (origin.MethodPointerCount - 1) * 8,
                evidence.MethodPointers[^1] + 8), expectedFresh: true);
        Check("unselected-native-invoker-changed", changes =>
            changes.Write64(origin.InvokersAddress + (origin.InvokerCount - 1) * 8,
                evidence.Invokers[^1] + 8), expectedFresh: true);
        if (evidence.AdjustorThunks.Length <= 1)
            throw new InvalidOperationException("The neutral input has no unselected consumed adjustor slot.");
        Check("unselected-native-adjustor-changed", changes =>
            changes.Write64(origin.AdjustorThunksAddress + (ulong)(evidence.AdjustorThunks.Length - 1) * 8,
                evidence.AdjustorThunks[^1] + 8), expectedFresh: true);
        // Private cached pointer-slot values are not consumed by this component.
        // This is a characterization, not a claimed provenance rejection.
        Check("unselected-private-pointer-cache-value-not-consumed", changes =>
            changes.Assign(() => pointers[^1], value => pointers[^1] = value, pointers[^1] + 8),
            expectedFresh: true, expectedSaved: true);
        Check("same-order-specification-container", changes =>
            changes.Assign(() => app.Metadata.methodSpecs, value => app.Metadata.methodSpecs = value, specRows.ToArray()),
            expectedFresh: true, expectedSaved: true);
        Check("same-order-function-container", changes =>
            changes.Assign(() => app.Metadata.genericMethodTables, value => app.Metadata.genericMethodTables = value, functionRows.ToArray()),
            expectedFresh: true, expectedSaved: true);
        Check("same-order-instantiation-container", changes => changes.Array(instancesField, instances.ToArray()),
            expectedFresh: true, expectedSaved: true);
        Check("same-order-method-pointer-container", changes => changes.Array(pointersField, pointers.ToArray()),
            expectedFresh: true, expectedSaved: true);

        // The second Object remains a competing original row even when its
        // attributes, modifiers or pinned state make it semantically ineligible.
        var neighborDescriptor = neighbor.Descriptors[0];
        foreach (var flag in new uint[] { 0, 1, 1u << 24, 1u << 30 })
        {
            Check("second-object-descriptor-flags-" + flag, changes =>
            {
                changes.DescriptorUnion(neighborDescriptor, canonical.Descriptors[0].Datapoint);
                changes.DescriptorFlags(neighborDescriptor, ((uint)Il2CppTypeEnum.IL2CPP_TYPE_OBJECT << 16) | flag);
            }, expectedSelected: null);
        }
        Check("interrupted-multi-edit-restoration", changes =>
        {
            changes.Write64(origin.InvokersAddress, evidence.Invokers[0] + 8);
            changes.Assign(() => app.Metadata.methodSpecs, value => app.Metadata.methodSpecs = value, specRows[..^1]);
        }, expectedFresh: true, expectedSaved: true, interrupt: true);

        // Saved native evidence must survive edits through every caller-owned
        // constructor array. Original metadata objects intentionally stay shared.
        var descriptorBytes = canonical.Descriptors[0].Bytes.ToArray();
        var copiedDescriptor = new X64GenericInstantiationTableProof.Descriptor(
            canonical.Descriptors[0].Original, canonical.Descriptors[0].Address, descriptorBytes);
        var argumentPointers = canonical.ArgumentPointers.ToArray();
        var descriptors = canonical.Descriptors.ToArray();
        descriptors[0] = copiedDescriptor;
        var copiedRow = new X64GenericInstantiationTableProof.Row(canonical.Ordinal, canonical.Address,
            canonical.Count, canonical.Arguments, argumentPointers, descriptors);
        var rows = evidence.Instantiations.Rows.ToArray();
        rows[canonical.Ordinal] = copiedRow;
        var copiedInstantiations = new X64GenericInstantiationTableProof.Evidence(
            evidence.Instantiations.Table, rows, evidence.Instantiations.CanonicalObjectOrdinal);
        var specifications = evidence.Specifications.ToArray();
        var functions = evidence.Functions.ToArray();
        var methodPointers = evidence.MethodPointers.ToArray();
        var invokers = evidence.Invokers.ToArray();
        var adjustors = evidence.AdjustorThunks.ToArray();
        var copiedEvidence = new X64GenericMethodTableProof.Evidence(origin, copiedInstantiations,
            specifications, functions, methodPointers, invokers, adjustors);

        descriptorBytes[0] ^= 1;
        argumentPointers[0] ^= 8;
        descriptors[0] = neighbor.Descriptors[0];
        rows[canonical.Ordinal] = neighbor;
        specifications[0] = specifications[^1];
        functions[0] = functions[^1];
        methodPointers[0] ^= 8;
        invokers[0] ^= 8;
        adjustors[0] ^= 8;
        var isolated = copiedDescriptor.Matches(canonical.Descriptors[0]) && copiedRow.Matches(canonical) &&
            copiedInstantiations.Matches(app, pe, index) && copiedEvidence.Matches(app, pe, index);
        results.Add(new("caller-array-alias-isolation", true, Fresh(), true, isolated,
            true, Selected(), Restored(), null));
        return results.ToArray();
    }

    private sealed class ControlledInterruption : Exception;

    private sealed class Mutation(PE pe, byte[] raw) : IDisposable
    {
        private readonly Stack<Action> _restores = new();

        internal void Assign<T>(Func<T> read, Action<T> write, T value)
        {
            var previous = read();
            _restores.Push(() => write(previous));
            write(value);
        }

        internal void Array(FieldInfo field, Array value) =>
            Assign(() => field.GetValue(pe), previous => field.SetValue(pe, previous), value);

        internal void Write64(ulong address, ulong value)
        {
            var offset = checked((int)pe.MapVirtualAddressToRaw(address, false));
            var previous = raw.AsSpan(offset, 8).ToArray();
            _restores.Push(() => previous.CopyTo(raw, offset));
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(offset, 8), value);
        }

        internal void Write32(ulong address, int value)
        {
            var offset = checked((int)pe.MapVirtualAddressToRaw(address, false));
            var previous = raw.AsSpan(offset, 4).ToArray();
            _restores.Push(() => previous.CopyTo(raw, offset));
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(offset, 4), value);
        }

        internal void DescriptorUnion(X64GenericInstantiationTableProof.Descriptor descriptor, ulong value)
        {
            Write64(descriptor.Address, value);
            Assign(() => descriptor.Original.Datapoint, previous => descriptor.Original.Datapoint = previous, value);
            Assign(() => descriptor.Original.Data.Dummy, previous => descriptor.Original.Data.Dummy = previous, value);
        }

        internal void DescriptorFlags(X64GenericInstantiationTableProof.Descriptor descriptor, uint bits)
        {
            var type = descriptor.Original;
            Write32(descriptor.Address + 8, unchecked((int)bits));
            Assign(() => type.Bits, previous => type.Bits = previous, bits);
            Assign(() => type.Attrs, previous => type.Attrs = previous, bits & 0xffff);
            Assign(() => type.Type, previous => type.Type = previous, (Il2CppTypeEnum)((bits >> 16) & 0xff));
            Assign(() => type.NumMods, previous => type.NumMods = previous, (bits >> 24) & 0x1f);
            Assign(() => type.Byref, previous => type.Byref = previous, (bits >> 29) & 1);
            Assign(() => type.Pinned, previous => type.Pinned = previous, (bits >> 30) & 1);
            Assign(() => type.ValueType, previous => type.ValueType = previous, bits >> 31);
        }

        public void Dispose()
        {
            while (_restores.TryPop(out var restore)) restore();
        }
    }

    private static FieldInfo PrivateField(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Fixture mutation field is unavailable: " + name);

    private static T Clone<T>(T value) where T : class => (T)typeof(object)
        .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, null)!;
}
