# Attribute array and property fixture

This separate authored assembly preserves the earlier attribute/parameter
fixture's denominator. It exercises populated, empty and null enum arrays;
object-owned enum arrays; named object properties containing Type, primitive
arrays, enum arrays and heterogeneous arrays; and named typed array properties
with populated, empty and null values.

Stage this directory beside the arithmetic fixture in a fresh ignored source
directory. Build with `Validation/run_fixture.py` using the exact Windows x64
Release IL2CPP profile. Keep source and original managed assemblies outside the
player-only recovery inputs. Run metadata recovery with `attributeanalyzer`,
then compare against the stripped managed oracle and supply the original
unstripped assembly to disclose stripping changes.

Measure the baseline before changing recovery. Attribute array element identity
must come from retained enum metadata or a declared owner signature, not from
its primitive storage width. A successful arithmetic behavior run makes no
method-body or property-execution claim for this declaration fixture.

The exact authored baseline retains three types, ten methods, seven fields,
four properties and twenty-five attributes with no stripped declaration
identity loss. Before the enum-array fix, object-owned enum arrays become byte
arrays in both a constructor argument and a named property. Native attribute
payloads retain the enum type in both cases; the storage element kind alone is
insufficient. Typed enum-array owners, null/empty arrays and the other named
properties already match their encoded oracles.

The raw declaration comparator also records core Type-name qualification
spelling changed by stripping. Keep this difference visible and separate from
the measured enum-identity errors.

After preferring the retained enum type when constructing array signatures,
the two enum-identity errors are gone. All twenty-five recovered attributes
decode successfully. The primary stripped-oracle comparison still reports one
attribute-group difference containing only the raw core Type-name
qualification spelling; it remains a failed gate. A separately reported
comparison against the original unstripped oracle has zero projection
differences. Neither result establishes recovered method bodies or behavior.

Run the optional exact-fixture serialization regression with an absolute input
path:

```sh
CPP2IL_ATTRIBUTE_ARRAY_FIXTURE_INPUT="$ATTRIBUTE_ARRAY_PLAYER_INPUT" \
  dotnet test --project Cpp2IL.Core.Tests -c Release --no-restore -- \
  --filter 'FullyQualifiedName~PreservesEnumArrayIdentityForObjectOwnersAndNamedProperties'
```

The test explicitly skips when the variable is absent and rejects mismatched
fixture declarations. It reads only the native player inputs, writes recovered
metadata and decodes the attribute blobs with System.Reflection.Metadata.
Checks include object-owned enum-array identity, enum values, typed null and
empty arrays, named property types and heterogeneous object-array elements.
