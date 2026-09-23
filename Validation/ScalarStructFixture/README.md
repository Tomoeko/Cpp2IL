# Scalar struct parameter fixture

`ScalarStructFixture` contains two sequential value types with one public instance
field each (`uint` and `ulong`), and four non-inlined static methods testing field
equality and unchecked addition. Parameters remain value types in the managed
signature. The independent driver runs 50 input pairs across zero, one, the maximum
signed value, the sign bit and the maximum unsigned value; sums are checked modulo
the declared width with exact integers.

```sh
python3 Validation/run_fixture.py --profile scalar-structs \
  --editor "$UNITY_WINDOWS_EDITOR" --wine "$WINE" \
  --toolchain-root "$WINDOWS_TOOLCHAIN" \
  --run-dir Files/validation/scalar-structs-baseline --stage run
```

`scalar-structs-negative` is a separate original fixture for rejection boundaries:
a padded UInt32 wrapper, a two-field aggregate, and an aggregate containing a managed
reference. Its 17 observations establish the original API behavior; they do not
claim successful recovery. Strict recovery of that assembly must remain rejected
by the bounded single-field parameter bridge.

The implemented recovery proof is deliberately narrow:

- The player is Windows PE x64 with eight-byte pointers; native operand width is
  established as 32 or 64 bits.
- The value is an original by-value parameter, not `this`, a by-reference value,
  a pointer, a hidden context argument or an unrelated local.
- Metadata establishes a concrete, non-generic, non-enum, blittable value type,
  sequential default layout, and an unboxed size equal to the native width.
- Exactly one instance field occupies offset zero and has the matching primitive
  Int32/UInt32 or Int64/UInt64 type. No unresolved marshaling or padding is accepted.
- Access to the field is legal from the emitted method. The initial boundary is
  a public field or a method in the field's own declaring type.

A proved projection loads the original struct parameter and its field. It must
not relabel the parameter or change its managed signature. Native arithmetic
width must survive lifting and analysis; missing width is not inferred merely
because the struct has one field. Exact-target native inspection, typed IL
verification, fresh Unity source compilation and rebuilt-player observations are
separate acceptance gates.

The four-method fixture passes player-only strict recovery, typed IL verification,
exact Unity 2021.3.35f1 source compilation, and a fresh Windows x64 IL2CPP Release
build. Both original and recovered players pass all 50 input pairs. A separate
declaration comparison preserves the three types, four methods, two fields, eight
parameters and three attributes. The three negative layout methods remain rejected
by strict recovery. These results establish this finite fixture scope, not general
struct ABI recovery or whole-program equivalence.

The underlying representation follows the [Microsoft x64 calling convention](https://learn.microsoft.com/en-us/cpp/build/x64-calling-convention?view=msvc-170#parameter-passing),
which passes eligible scalar-sized aggregates in integer argument registers.
