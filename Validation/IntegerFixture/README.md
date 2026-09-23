# Unsigned integer comparison fixture

This independent synthetic assembly contains eight static, non-inlined predicates:
less than, greater than, less than or equal, and greater than or equal for `uint`
and `ulong`. There is no instance constructor or static initializer. It is a
separate recovery scope from the original four-method arithmetic fixture.

The validation driver lives in `Validation/IntegerHarness`. It invokes each
predicate on every pair of five values at each width: zero, one, the maximum
signed value, the sign bit, and the maximum unsigned value. The report contains
50 operand pairs and 200 Boolean observations. A Python oracle compares exact
integers; UInt64 values never pass through floating point. The shared JSON
serializer emits full decimal integer values.

Use the supplied exact editor with the existing harness:

```sh
python3 Validation/run_fixture.py --profile integers \
  --editor "$UNITY_WINDOWS_EDITOR" --wine "$WINE" \
  --toolchain-root "$WINDOWS_TOOLCHAIN" \
  --run-dir Files/validation/integer-baseline --stage run --timeout 1200
```

After building Cpp2IL, the full round-trip runner also accepts `--profile integers`.
Replacement source must expose the `IntegerFixture.IntegerComparisons` API in
assembly `IntegerFixture`. The driver is supplied independently, outside the
selected recovery assembly. The runner's generic player executable remains named
`RecoveryFixture.exe`; the receipt identifies the actual fixture profile and
selected assembly.

The player input consists only of shipped binaries and metadata. Original source,
managed assemblies, generated C++, and debug symbols are validation oracles and
must not be supplied to player-only recovery. Recovered methods must pass strict
recovery, managed IL verification, exact-editor source compilation, a Windows x64
IL2CPP Release rebuild, and the player observations before those stages are
reported as passed. The finite vectors do not establish arbitrary whole-program
or source equivalence.

The acceptance-boundary tests can run without Unity:

```sh
python3 Validation/test_integer_fixture.py
```

These tests reject missing coverage, signed comparisons at unsigned boundaries,
precision-losing operands and numeric substitutions for JSON booleans. They do
not replace the native integration run.
