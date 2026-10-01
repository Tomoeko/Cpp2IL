using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

// Mutations affect only the loaded fixture's arrays and native bytes, never its files.
// The helper checks selected registration provenance, not recovered method behavior.
internal static class X64SelectedGenericRegistrationControls
{
    internal sealed record Result(string Name, bool ExpectedAcceptance, bool Accepted,
        bool Restored, string? Exception);

    internal static Result[] Run(ApplicationAnalysisContext app, Func<Cpp2IlMethodRef, ulong, bool> identify)
    {
        var pe = (PE)app.Binary;
        var metadata = app.Metadata;
        var definition = app.GetAssemblyByName("NativeDirectGenericReferenceInvocationFixture")!
            .Types.Single(type => type.Name == "Relay").Methods.Single(method => method.Name == "Echo").Definition!;
        var reference = pe.ConcreteGenericMethods[definition].Single(value =>
            value.MethodGenericParams.Length == 1 &&
            value.MethodGenericParams[0].Type == Il2CppTypeEnum.IL2CPP_TYPE_OBJECT);
        var pointer = reference.GenericVariantPtr;
        if (!pe.TryGetGenericMethodRegistration(reference, out var binding) ||
            !pe.TryGetGenericMethodTableRegistration(out var tables) ||
            !pe.TryGetGenericInstantiationTableRegistration(out var instantiations))
            throw new InvalidOperationException("Original registration provenance is unavailable.");

        var specification = metadata.AllGenericMethodSpecs[binding.SpecificationIndex];
        var function = metadata.genericMethodTables[binding.TableIndex];
        var instantiationIndex = specification.methodIndexIndex.Value;
        if (!pe.TryGetGenericInstantiationRegistration(instantiationIndex, out var instantiation))
            throw new InvalidOperationException("Original selected instantiation is unavailable.");

        var instancesField = PrivateField(typeof(Il2CppBinary), "_genericInsts");
        var pointersField = PrivateField(typeof(Il2CppBinary), "_genericMethodPointers");
        var instances = (Il2CppGenericInst[])instancesField.GetValue(pe)!;
        var pointers = (ulong[])pointersField.GetValue(pe)!;
        var selectedInstance = instances[instantiationIndex];
        var raw = (byte[])PrivateField(typeof(PE), "raw").GetValue(pe)!;
        var results = new List<Result>();
        if (!identify(reference, pointer))
            throw new InvalidOperationException("Clean selected registration was rejected.");

        void Check(string name, Func<Action> mutate, bool expectedAcceptance = false)
        {
            if (!identify(reference, pointer))
                throw new InvalidOperationException("Registration was not restored before " + name);
            var restore = mutate();
            var accepted = false;
            string? exception = null;
            try
            {
                accepted = identify(reference, pointer);
            }
            catch (Exception failure)
            {
                exception = failure.GetType().Name;
            }
            finally
            {
                restore();
            }
            var restored = identify(reference, pointer);
            results.Add(new Result(name, expectedAcceptance, accepted, restored, exception));
            if (!restored)
                throw new InvalidOperationException("Registration restore failed: " + name);
        }

        int Offset(ulong address) => checked((int)pe.MapVirtualAddressToRaw(address, false));

        Action Set64(ulong address, ulong value)
        {
            var offset = Offset(address);
            var previous = raw.AsSpan(offset, 8).ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(offset, 8), value);
            return () => previous.CopyTo(raw, offset);
        }

        Action Set32(ulong address, int value)
        {
            var offset = Offset(address);
            var previous = raw.AsSpan(offset, 4).ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(offset, 4), value);
            return () => previous.CopyTo(raw, offset);
        }

        Action CopyBytes(ulong source, ulong destination, int length)
        {
            var sourceOffset = Offset(source);
            var destinationOffset = Offset(destination);
            var previous = raw.AsSpan(destinationOffset, length).ToArray();
            raw.AsSpan(sourceOffset, length).CopyTo(raw.AsSpan(destinationOffset, length));
            return () => previous.CopyTo(raw, destinationOffset);
        }

        // Coherent native-count and cache-length reductions must still conflict
        // with the original denominator, even when the selected ordinal survives.
        if (binding.SpecificationIndex >= tables.SpecificationCount - 1 ||
            binding.TableIndex >= tables.FunctionCount - 1 ||
            function.methodIndex >= pointers.Length - 1 || instantiationIndex >= instances.Length - 1)
            throw new InvalidOperationException("The fixture has no unselected registration tail.");

        Check("coherent-original-specification-tail-removed", () =>
        {
            var previous = metadata.methodSpecs;
            var restore = Set64(tables.MetadataRegistrationAddress + 64, (ulong)tables.SpecificationCount - 1);
            metadata.methodSpecs = previous[..^1];
            return () => { metadata.methodSpecs = previous; restore(); };
        });
        Check("coherent-original-function-tail-removed", () =>
        {
            var previous = metadata.genericMethodTables;
            var restore = Set64(tables.MetadataRegistrationAddress + 32, (ulong)tables.FunctionCount - 1);
            metadata.genericMethodTables = previous[..^1];
            return () => { metadata.genericMethodTables = previous; restore(); };
        });
        Check("coherent-original-method-pointer-tail-removed", () =>
        {
            var restore = Set64(tables.CodeRegistrationAddress + 16, tables.MethodPointerCount - 1);
            pointersField.SetValue(pe, pointers[..^1]);
            return () => { pointersField.SetValue(pe, pointers); restore(); };
        });
        Check("coherent-original-instantiation-tail-removed", () =>
        {
            var restore = Set64(tables.MetadataRegistrationAddress + 16, (ulong)instantiations.Count - 1);
            instancesField.SetValue(pe, instances[..^1]);
            return () => { instancesField.SetValue(pe, instances); restore(); };
        });
        Check("original-invoker-count-lowered", () =>
            Set64(tables.CodeRegistrationAddress + 40, tables.InvokerCount - 1));
        Check("original-invoker-table-moved", () =>
            Set64(tables.CodeRegistrationAddress + 48, tables.InvokersAddress + 8));
        Check("original-adjustor-table-moved", () =>
            Set64(tables.CodeRegistrationAddress + 32, tables.AdjustorThunksAddress ^ 8));
        Check("equivalent-selected-specification-instance", () =>
        {
            metadata.methodSpecs[binding.SpecificationIndex] = Clone(specification);
            return () => metadata.methodSpecs[binding.SpecificationIndex] = specification;
        });
        Check("equivalent-selected-function-instance", () =>
        {
            metadata.genericMethodTables[binding.TableIndex] = Clone(function);
            return () => metadata.genericMethodTables[binding.TableIndex] = function;
        });
        Check("equivalent-selected-instantiation-instance", () =>
        {
            instances[instantiationIndex] = Clone(selectedInstance);
            return () => instances[instantiationIndex] = selectedInstance;
        });
        Check("coherent-selected-function-invoker", () =>
        {
            var previous = function.invokerIndex;
            var changed = previous == 0 ? 1 : 0;
            var restore = Set32(tables.FunctionsAddress + (ulong)binding.TableIndex * 16 + 8, changed);
            function.invokerIndex = changed;
            return () => { function.invokerIndex = previous; restore(); };
        });
        Check("coherent-selected-specification-class-context", () =>
        {
            var previous = specification.classIndexIndex;
            var restore = Set32(tables.SpecificationsAddress + (ulong)binding.SpecificationIndex * 12 + 4,
                instantiationIndex);
            specification.classIndexIndex =
                Il2CppVariableWidthIndex<Il2CppGenericInst>.MakeTemporaryForFixedWidthUsage(instantiationIndex);
            return () => { specification.classIndexIndex = previous; restore(); };
        });

        var neighbor = Enumerable.Range(0, instances.Length).Where(index => index != instantiationIndex)
            .Select(index => pe.TryGetGenericInstantiationRegistration(index, out var value) ? value : default)
            .First(value => value.Address != 0 && value.ArgumentCount >= instantiation.ArgumentCount &&
                value.ArgumentsAddress != instantiation.ArgumentsAddress);
        Check("coherent-selected-instantiation-slot-moved", () =>
        {
            var rowRestore = CopyBytes(instantiation.Address, neighbor.Address, 16);
            var slotRestore = Set64(instantiations.Address + (ulong)instantiationIndex * 8, neighbor.Address);
            return () => { slotRestore(); rowRestore(); };
        });
        Check("coherent-selected-instantiation-argument-array-moved", () =>
        {
            var argumentRestore = CopyBytes(instantiation.ArgumentsAddress, neighbor.ArgumentsAddress,
                checked((int)instantiation.ArgumentCount * 8));
            var rowRestore = Set64(instantiation.Address + 8, neighbor.ArgumentsAddress);
            var previous = selectedInstance.pointerStart;
            selectedInstance.pointerStart = neighbor.ArgumentsAddress;
            return () => { selectedInstance.pointerStart = previous; rowRestore(); argumentRestore(); };
        });
        Check("selected-native-instantiation-count-wide", () =>
            Set64(instantiation.Address, instantiation.ArgumentCount + (1ul << 32)));
        Check("raw-specification-count-wide", () =>
            Set64(tables.MetadataRegistrationAddress + 64, (ulong)tables.SpecificationCount + (1ul << 32)));

        // A new container with the same ordered objects preserves their identities.
        Check("same-order-specification-container", () =>
        {
            var previous = metadata.methodSpecs;
            metadata.methodSpecs = (Il2CppMethodSpec[])previous.Clone();
            return () => metadata.methodSpecs = previous;
        }, expectedAcceptance: true);
        Check("same-order-function-container", () =>
        {
            var previous = metadata.genericMethodTables;
            metadata.genericMethodTables = (Il2CppGenericMethodFunctionsDefinitions[])previous.Clone();
            return () => metadata.genericMethodTables = previous;
        }, expectedAcceptance: true);
        Check("same-order-instantiation-container", () =>
        {
            instancesField.SetValue(pe, instances.Clone());
            return () => instancesField.SetValue(pe, instances);
        }, expectedAcceptance: true);
        return results.ToArray();
    }

    private static FieldInfo PrivateField(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Fixture mutation field is unavailable: " + name);

    private static T Clone<T>(T value) where T : class => (T)typeof(object)
        .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, null)!;
}
