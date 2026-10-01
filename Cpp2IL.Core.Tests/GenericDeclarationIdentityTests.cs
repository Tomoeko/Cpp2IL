using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.Analysis;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class GenericDeclarationIdentityTests
{
    [Test]
    public void OriginalRowsAndActualParameterContextsSurviveOnlyIdentityPreservingChanges()
    {
        const string environment = "CPP2IL_NATIVE_DIRECT_GENERIC_REFERENCE_INVOCATION_FIXTURE_INPUT";
        var input = Environment.GetEnvironmentVariable(environment);
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set " + environment + " to the neutral exact player-input directory.");

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
                Directory.EnumerateFiles(input!, "global-metadata.dat", SearchOption.AllDirectories).Single(),
                UnityVersion.Parse("2021.3.35f1"));
            var results = GenericDeclarationIdentityControls.Run(Cpp2IlApi.CurrentAppContext!);
            Assert.That(results, Has.Length.EqualTo(31));
            Assert.Multiple(() =>
            {
                foreach (var result in results)
                {
                    TestContext.Out.WriteLine(result.Name + ": fresh=" + result.FreshAccepted +
                        ", saved=" + result.SavedAccepted + ", restored=" + result.Restored);
                    Assert.That(result.FreshAccepted, Is.EqualTo(result.ExpectedFreshAcceptance), result.Name);
                    Assert.That(result.SavedAccepted, Is.EqualTo(result.ExpectedSavedAcceptance), result.Name);
                    Assert.That(result.Restored, Is.True, result.Name);
                    Assert.That(result.Exception, Is.EqualTo(result.ExpectedException), result.Name);
                }
            });
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }
}

// All mutations are in-memory and restored before the next assertion. The player
// remains the complete fixture input; these controls make no recovery admission.
internal static class GenericDeclarationIdentityControls
{
    internal sealed record Result(string Name, bool ExpectedFreshAcceptance,
        bool ExpectedSavedAcceptance, bool FreshAccepted, bool SavedAccepted,
        bool Restored, string? Exception, string? ExpectedException);

    internal static Result[] Run(ApplicationAnalysisContext app)
    {
        var owner = app.GetAssemblyByName("NativeDirectGenericReferenceInvocationFixture")!
            .Types.Single(type => type.Name == "Relay").Methods.Single(method => method.Name == "Echo");
        var metadata = app.Metadata;
        var saved = OriginalGenericDeclarationIdentityProof.TryIdentify(owner)
            ?? throw new InvalidOperationException("The clean original declaration was rejected.");
        var table = saved.Table;
        var containerIndex = saved.Container.Index;
        var parameterIndex = saved.Parameters[0].Origin.Index;
        var containerField = PrivateField(typeof(Il2CppMetadata), "genericContainers");
        var parameterField = PrivateField(typeof(Il2CppMetadata), "genericParameters");
        var containers = (Il2CppGenericContainer[])containerField.GetValue(metadata)!;
        var parameters = (Il2CppGenericParameter[])parameterField.GetValue(metadata)!;
        var container = containers[containerIndex];
        var parameter = parameters[parameterIndex];
        var context = owner.GenericParameters.Single();
        var results = new List<Result>();
        var rollback = new Stack<Action>();

        bool Fresh() => OriginalGenericDeclarationIdentityProof.TryIdentify(owner) != null;

        void Check(string name, Func<Action> mutate, bool expectedFresh = false, bool expectedSaved = false,
            string? expectedException = null)
        {
            if (!Fresh() || !saved.Matches())
                throw new InvalidOperationException("Declaration was not restored before " + name);
            var fresh = false;
            var retained = false;
            string? exception = null;
            try
            {
                rollback.Push(mutate());
                fresh = Fresh();
                retained = saved.Matches();
            }
            catch (Exception failure)
            {
                exception = failure.GetType().Name;
            }
            finally
            {
                while (rollback.TryPop(out var restore)) restore();
            }
            var restored = Fresh() && saved.Matches();
            results.Add(new(name, expectedFresh, expectedSaved, fresh, retained, restored, exception, expectedException));
            if (!restored)
                throw new InvalidOperationException("Declaration restore failed: " + name);
        }

        Action Write32(long offset, int value)
        {
            var previous = metadata.ReadByteArrayAtRawAddress(offset, sizeof(int));
            Action restore = () => { metadata.BaseStream.Position = offset; metadata.BaseStream.Write(previous); };
            rollback.Push(restore);
            metadata.BaseStream.Position = offset;
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            metadata.BaseStream.Write(bytes);
            return restore;
        }

        Action WriteByte(long offset, byte value)
        {
            var previous = metadata.ReadByteArrayAtRawAddress(offset, 1)[0];
            Action restore = () => { metadata.BaseStream.Position = offset; metadata.BaseStream.WriteByte(previous); };
            rollback.Push(restore);
            metadata.BaseStream.Position = offset;
            metadata.BaseStream.WriteByte(value);
            return restore;
        }

        Check("equal-valued-selected-container-replacement", () =>
        {
            containers[containerIndex] = Clone(container);
            return () => containers[containerIndex] = container;
        });
        Check("equal-valued-selected-parameter-replacement", () =>
        {
            parameters[parameterIndex] = Clone(parameter);
            return () => parameters[parameterIndex] = parameter;
        });
        Check("coherent-row-and-context-parameter-replacement", () =>
        {
            Action restore = () => { parameters[parameterIndex] = parameter; owner.GenericParameters[0] = context; };
            rollback.Push(restore);
            var replacement = Clone(parameter);
            parameters[parameterIndex] = replacement;
            owner.GenericParameters[0] = new(replacement, owner);
            return restore;
        });
        Check("parsed-row-context-replaced-before-fresh-capture", () =>
        {
            owner.GenericParameters[0] = new(parameter, owner);
            return () => owner.GenericParameters[0] = context;
        }, expectedFresh: true);
        Check("value-equivalent-context-without-parsed-row", () =>
        {
            owner.GenericParameters[0] = new(context.Name, context.Index, context.Type, context.Attributes, owner);
            return () => owner.GenericParameters[0] = context;
        });
        Check("null-parameter-context-is-unavailable", () =>
        {
            owner.GenericParameters[0] = null!;
            return () => owner.GenericParameters[0] = context;
        });
        Check("context-backed-by-equivalent-nonoriginal-row", () =>
        {
            owner.GenericParameters[0] = new(Clone(parameter), owner);
            return () => owner.GenericParameters[0] = context;
        });
        Check("coherent-selected-container-owner-change", () =>
        {
            var previous = container.ownerIndex;
            var restore = Write32(table.ContainersOffset + (long)containerIndex * 16, previous ^ 1);
            container.ownerIndex = previous ^ 1;
            return () => { container.ownerIndex = previous; restore(); };
        });
        Check("coherent-selected-parameter-name-index-change", () =>
        {
            var previous = parameter.nameIndex;
            var restore = Write32(table.ParametersOffset + (long)parameterIndex * 16 + 4, previous ^ 1);
            parameter.nameIndex = previous ^ 1;
            return () => { parameter.nameIndex = previous; restore(); };
        });
        Check("selected-parameter-flags-change", () =>
        {
            var previous = parameter.flags;
            parameter.flags ^= 4;
            return () => parameter.flags = previous;
        });
        Check("selected-parameter-position-change", () =>
        {
            var previous = parameter.genericParameterIndexInOwner;
            parameter.genericParameterIndexInOwner++;
            return () => parameter.genericParameterIndexInOwner = previous;
        });
        Check("selected-container-arity-change", () =>
        {
            container.genericParameterCount++;
            return () => container.genericParameterCount--;
        });
        Check("selected-container-kind-change", () =>
        {
            container.isGenericMethod = !container.isGenericMethod;
            return () => container.isGenericMethod = !container.isGenericMethod;
        });
        Check("original-unselected-container-replacement", () =>
        {
            var ordinal = containerIndex == 0 ? 1 : 0;
            var previous = containers[ordinal];
            containers[ordinal] = Clone(previous);
            return () => containers[ordinal] = previous;
        });
        Check("original-unselected-parameter-replacement", () =>
        {
            var ordinal = parameterIndex == 0 ? 1 : 0;
            var previous = parameters[ordinal];
            parameters[ordinal] = Clone(previous);
            return () => parameters[ordinal] = previous;
        });
        Check("container-table-tail-removed", () =>
        {
            containerField.SetValue(metadata, containers[..^1]);
            return () => containerField.SetValue(metadata, containers);
        });
        Check("parameter-table-tail-removed", () =>
        {
            parameterField.SetValue(metadata, parameters[..^1]);
            return () => parameterField.SetValue(metadata, parameters);
        });
        Check("coherent-container-section-moved", () =>
        {
            var section = metadata.metadataHeader.genericContainers;
            var restore = Write32(120, section.Offset + 16);
            section.Offset += 16;
            return () => { section.Offset -= 16; restore(); };
        });
        Check("serialized-parameter-section-size-change", () => Write32(108, table.ParametersBytes - 16));
        Check("original-constraint-index-change", () =>
        {
            if (metadata.constraintIndices.Length == 0)
                throw new InvalidOperationException("The complete fixture has no constraint control row.");
            var previous = metadata.constraintIndices[0];
            var restore = Write32(table.ConstraintsOffset, previous.Value ^ 1);
            metadata.constraintIndices[0] = Il2CppVariableWidthIndex<Il2CppType>
                .MakeTemporaryForFixedWidthUsage(previous.Value ^ 1);
            return () => { metadata.constraintIndices[0] = previous; restore(); };
        });
        Check("parameter-context-name-override", () =>
        {
            var previous = context.OverrideName;
            context.Name += "Changed";
            return () => context.OverrideName = previous;
        });
        Check("cached-parameter-name-does-not-hide-raw-change", () =>
        {
            var address = metadata.metadataHeader.@string.Offset + (long)parameter.nameIndex;
            var previous = metadata.ReadByteArrayAtRawAddress(address, 1)[0];
            return WriteByte(address, (byte)(previous ^ 1));
        });
        Check("parameter-context-attribute-override", () =>
        {
            var previous = context.OverrideAttributes;
            context.Attributes ^= GenericParameterAttributes.ReferenceTypeConstraint;
            return () => context.OverrideAttributes = previous;
        });
        Check("parameter-context-constraint-list-change", () =>
        {
            context.ConstraintTypes.Add(app.SystemTypes.SystemObjectType);
            return () => context.ConstraintTypes.RemoveAt(context.ConstraintTypes.Count - 1);
        });
        Check("same-order-container-array", () =>
        {
            containerField.SetValue(metadata, containers.Clone());
            return () => containerField.SetValue(metadata, containers);
        }, expectedFresh: true, expectedSaved: true);
        Check("same-order-parameter-array", () =>
        {
            parameterField.SetValue(metadata, parameters.Clone());
            return () => parameterField.SetValue(metadata, parameters);
        }, expectedFresh: true, expectedSaved: true);
        Check("same-order-parameter-context-list", () =>
        {
            var field = PrivateField(typeof(MethodAnalysisContext), "_genericParameters");
            var previous = field.GetValue(owner);
            field.SetValue(owner, owner.GenericParameters.ToList());
            return () => field.SetValue(owner, previous);
        }, expectedFresh: true, expectedSaved: true);
        Check("lazy-parameter-index-is-not-serialized-identity", () =>
        {
            var property = typeof(Il2CppGenericParameter).GetProperty(nameof(Il2CppGenericParameter.Index))!;
            var previous = property.GetValue(parameter);
            property.SetValue(parameter, Il2CppVariableWidthIndex<Il2CppGenericParameter>
                .MakeTemporaryForFixedWidthUsage(-1));
            return () => property.SetValue(parameter, previous);
        }, expectedFresh: true, expectedSaved: true);
        Check("saved-parameter-binding-array-is-isolated", () =>
        {
            var aliases = saved.Parameters.ToArray();
            var copy = new OriginalGenericDeclarationIdentityProof.Evidence(owner, metadata, table,
                saved.Container, saved.Definition, aliases);
            aliases[0] = new(aliases[0].Origin with { Flags = 0 }, context, [], []);
            if (!copy.Matches()) throw new InvalidOperationException("Saved parameter bindings retained an array alias.");
            return () => { };
        }, expectedFresh: true, expectedSaved: true);
        Check("saved-constraint-array-captures-are-isolated", () =>
        {
            OriginalGenericDeclarationIdentityProof.ParameterBinding? selected = null;
            for (var ordinal = 0; ordinal < table.ContainerCount && selected == null; ordinal++)
            {
                if (!metadata.TryGetGenericContainerOrigin(ordinal, out var row)) continue;
                HasGenericParameters? candidate = row.IsGenericMethod
                    ? app.ResolveContextForMethod(metadata.methodDefs[row.OwnerIndex])
                    : app.ResolveContextForType(metadata.typeDefs[row.OwnerIndex]);
                if (candidate == null || OriginalGenericDeclarationIdentityProof.TryIdentify(candidate) is not { } proof)
                    continue;
                selected = proof.Parameters.ToArray().FirstOrDefault(binding => binding.ConstraintIndices.Length != 0);
            }
            if (selected == null) throw new InvalidOperationException("The complete input lacks a captured constraint binding.");
            var indices = selected.ConstraintIndices.ToArray();
            var contexts = selected.ConstraintContexts.ToArray();
            var copy = new OriginalGenericDeclarationIdentityProof.ParameterBinding(selected.Origin,
                selected.Context, indices, contexts);
            indices[0] ^= 1;
            contexts[0] = context;
            if (!copy.ConstraintIndices.SequenceEqual(selected.ConstraintIndices))
                throw new InvalidOperationException("Saved constraint values retained array aliases.");
            for (var index = 0; index < copy.ConstraintContexts.Length; index++)
                if (!ReferenceEquals(copy.ConstraintContexts[index], selected.ConstraintContexts[index]))
                    throw new InvalidOperationException("Saved constraint contexts retained array aliases.");
            return () => { };
        }, expectedFresh: true, expectedSaved: true);
        Check("injected-mutation-failure-restores-all-edits", () =>
        {
            rollback.Push(() => parameters[parameterIndex] = parameter);
            parameters[parameterIndex] = Clone(parameter);
            _ = Write32(table.ParametersOffset + (long)parameterIndex * 16 + 4, parameter.nameIndex ^ 1);
            throw new InvalidOperationException("Injected mutation failure.");
        }, expectedException: nameof(InvalidOperationException));
        return results.ToArray();
    }

    private static FieldInfo PrivateField(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Fixture mutation field is unavailable: " + name);

    private static T Clone<T>(T value) where T : class => (T)typeof(object)
        .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, null)!;
}
