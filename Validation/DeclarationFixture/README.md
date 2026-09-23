# Declaration fidelity fixture

This synthetic assembly tests metadata recovery separately from method bodies:
nested/generic types and constraints, signed enum values, custom attributes,
overloads/accessibility, ref/out/default parameters, struct layout and marshaling,
properties, events, delegates, interfaces and overrides.

`link.xml` preserves the fixture. The original exact-target baseline retained
10 types, 35 methods, 25 fields, 7 properties, 2 events, 6 generic parameters and
27 custom attributes. Always compare unstripped and stripped originals again;
preservation in one build is not a guarantee about another build.

Stage both public fixtures in a fresh ignored directory:

```sh
python3 - <<'PY'
from pathlib import Path
import shutil
source = Path("Files/validation/declaration-source")
shutil.copytree("Validation/Fixture", source)
shutil.copytree("Validation/DeclarationFixture", source / "Declarations")
PY

python3 Validation/run_fixture.py --editor "$UNITY_WINDOWS_EDITOR" --wine "$WINE" \
  --toolchain-root "$WINDOWS_TOOLCHAIN" \
  --source-dir Files/validation/declaration-source \
  --run-dir Files/validation/declaration-player --stage run --timeout 600
```

This creates an authored baseline. The behavior driver exercises only the
arithmetic fixture; it makes no behavior claim for the declaration fixture.
Give Cpp2IL only the resulting `player-input/` directory:

```sh
dotnet "$CPP2IL" --game-path "$DECLARATION_RUN/player-input" \
  --exe-name RecoveryFixture --use-processor attributeanalyzer \
  --output-as dll_default --output-to "$RECOVERED_DLLS"
```

`dll_default` supplies placeholder bodies. `attributeanalyzer` populates
custom-attribute values. Neither stage establishes method-body recovery.

The independent comparer reads PE/CLI metadata with `System.Reflection.Metadata`
without loading or executing input assemblies:

```sh
dotnet build Validation/DeclarationComparer/DeclarationComparer.csproj -c Release

dotnet Validation/DeclarationComparer/bin/Release/net10.0/DeclarationComparer.dll \
  --oracle "$DECLARATION_RUN/project/Library/Bee/artifacts/WinPlayerBuildProgram/ManagedStripped/DeclarationFixture.dll" \
  --candidate "$RECOVERED_DLLS/DeclarationFixture.dll" \
  --unstripped "$DECLARATION_RUN/project/Library/ScriptAssemblies/DeclarationFixture.dll" \
  --reference-dir "$UNITY_MANAGED_REFERENCE_DIR" \
  --output Files/validation/declaration-comparison
```

Supply the exact Unity .NET 4.x managed reference directory. References decode
enum-valued attributes; there is no host-framework fallback. Raw reports and
input fingerprints remain under ignored `Files/`.

The comparison retains identities, calling conventions, signatures, generic
constraints, flags, constants, layout, marshal descriptors, accessor/override
relationships and decoded attributes, including the selected constructor's
declaring type and signature. Constructor identity matters even when boxed
arguments decode to the same value as another overload. Metadata row order, tokens, MVIDs,
method bodies/RVAs and debug symbols are explicitly outside its scope.
Multi-module assemblies, resources, exported-type forwarding, security
declarations and every ECMA-335 form are not yet qualified.

`report.json` lists every missing, unexpected or changed fact and both sets of
counts. Its separate stripping section reports lost/introduced identities and
all changes between unstripped and stripped originals. Decode failures fail the
result even when remaining facts match. Zero differences establishes only the
declared projection, not valid IL, Unity compilation or behavior.

For independent layout observations, also copy `Validation/DeclarationProbe/`
into its own subdirectory beside `Declarations` before building. Set the child
process environment variable `CPP2IL_DECLARATION_LAYOUT_DIRECTORY` to a new
ignored output directory in the editor/player's path format. The probe writes
separate `editor-layout.json` and `player-layout.json` reports containing
`StructLayoutAttribute`, `Marshal.SizeOf/OffsetOf` and reflected `MarshalAs`
observations. It does not change the fixture's declarations or hide raw metadata
differences. A raw ClassLayout value may be unavailable after IL2CPP conversion
even when native runtime behavior is reproducible; report those claims separately.

The exact-target controlled build observed `Packet` with pack 4 and size 16 in
both Editor and player. `Sequence` reported pack 2, size 0 in the Editor and
pack 2, size 6 in the IL2CPP player; both reported marshaled size 6 and field
offsets 0, 2 and 4. The Editor reflected `MarshalAs(U1)` on `Enabled`, while
the player returned no marshaling attribute. These are separate observations,
not permission to omit a raw declaration mismatch. Adding the probe preserved
all 35 declaration methods and produced zero declaration changes or stripping
losses in the controlled baseline.

Run bounded comparer mutation checks without Unity or Wine:

```sh
python3 Validation/test_declaration_comparer.py
```

These compile independent .NET test assemblies to detect layout, marshaling,
constant, attribute, overloaded attribute constructor, constraint and
default-parameter changes, and to verify the intentional exclusion of method
bodies. They are separate from exact-Unity
fixture validation.

External enum attribute decoding resolves the complete declared assembly identity
against explicitly supplied reference directories. Multiple installed versions may
coexist, but exactly one must match name, version, culture and public-key token.
The comparer rejects missing or duplicate exact matches instead of selecting a
reference by directory order. Mutation checks cover mixed-version search order,
wrong version/culture and duplicate matching files.
