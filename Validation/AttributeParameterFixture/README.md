# Attribute and parameter fixture

This separate synthetic assembly exercises overloaded object/string attribute
constructors; boxed enums, Type values, typed and heterogeneous arrays, nulls
and empty arrays; ref/out/in parameters, mutable and readonly byref returns;
decimal, enum, null and optional defaults; params arrays and return/parameter
attributes.

Copy it into its own subdirectory beside the arithmetic fixture in a fresh
ignored source directory. Build with `Validation/run_fixture.py` using the
exact Windows x64 Release IL2CPP profile. Keep source, unstripped managed
assemblies and stripped managed assemblies outside the player-only recovery
inputs. Run metadata recovery with `attributeanalyzer`, then use the declaration
comparer against the stripped oracle and supply the unstripped original to
report stripping losses separately.

Record mismatches before changing the recovery implementation. Matching
declarations do not establish method-body recovery, parameter behavior or
runtime dispatch correctness. The arithmetic harness's observations make no
behavior claim for this assembly.

The authored exact-profile baseline has eight types, fifteen methods, eight
fields, sixteen parameter rows and twenty-two custom attributes. Stripping
removes no declaration identities in this fixture, but shortens serialized
core-library Type names inside attributes. The comparer records that spelling
change explicitly.

The bounded writer fix preserves named members' declared types and boxes Type
and array values when the owner is object. The resulting twenty emitted
attributes decode without errors, including overloaded constructor identities,
boxed enums, heterogeneous arrays and named null arrays. The complete
comparison still fails with eleven fact differences: ten arise from the two
missing return rows/attributes and the readonly return signature, and one
records the serialized Type-name qualification difference. This is not a
complete declaration-recovery pass.

The native metadata uses the same return-type record for the mutable and
readonly byref methods, with no custom modifiers. Neither original return-row
token appears in the assembly's native attribute ranges. Preserve these
measured losses rather than inventing a readonly modifier or return attribute.
The source expression `(int[])null` passed to the object constructor becomes a
string-null attribute argument in the original managed assembly already; it
is not evidence of a native array-type erasure.

Run the optional independent serialization regression with an absolute input
path:

```sh
CPP2IL_ATTRIBUTE_PARAMETER_FIXTURE_INPUT="$ATTRIBUTE_PARAMETER_PLAYER_INPUT" \
  dotnet test --project Cpp2IL.Core.Tests -c Release --no-restore -- \
  --filter 'FullyQualifiedName~AttributeParameterEmissionTests'
```

The test skips when the variable is absent and rejects missing or mismatched
fixture inputs. It recovers native attribute payloads, serializes them and
checks their contents using System.Reflection.Metadata without executing the
input assemblies. It distinguishes null from empty arrays and uses a synthetic
model mutation to verify that a null array with an object owner and no element
type fails explicitly with `ATTRIBUTE001`; no element type is guessed. Return
attributes, source compilation and behavior remain separate validation gaps.
