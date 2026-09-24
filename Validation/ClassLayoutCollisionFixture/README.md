# Declared versus effective class size

This synthetic fixture uses matching public value types and reference classes.
Within each group, field signatures and pack stay constant while the declared
`StructLayout.Size` is omitted, set to the six-byte natural size, or increased.
The three byte/short/byte fields have relative offsets 0, 2 and 4 under pack 2.
The value-type control declares eight bytes; the reference-class control declares
32 bytes to make the native and header-inclusive instance size increase clear.

Stage this directory beside `Validation/Fixture/` inside a new ignored source
directory. Build it through `Validation/run_fixture.py` with the supplied
Unity 2021.3.35f1 Windows editor, the existing Wine prefix and the Windows x64
Release IL2CPP profile. Use `--stage build`; the arithmetic fixture supplies
the runner's editor behavior check. Keep the project and player under `Files/`.

```sh
python3 - <<'PY'
from pathlib import Path
import shutil
source = Path("Files/validation/class-layout-class-source-01")
shutil.copytree("Validation/Fixture", source)
shutil.copytree("Validation/ClassLayoutCollisionFixture", source / "ClassLayoutCollision")
PY

python3 Validation/run_fixture.py --editor "$UNITY_WINDOWS_EDITOR" --wine "$WINE" \
  --toolchain-root "$WINDOWS_TOOLCHAIN" \
  --source-dir Files/validation/class-layout-class-source-01 \
  --run-dir Files/validation/class-layout-class-collision-01 --stage build --timeout 1200

dotnet build Validation/ClassLayoutCollisionInspector/ClassLayoutCollisionInspector.csproj -c Release
dotnet Validation/ClassLayoutCollisionInspector/bin/Release/net10.0/ClassLayoutCollisionInspector.dll \
  Files/validation/class-layout-class-collision-01/player-input \
  Files/validation/class-layout-class-collision-01/project/Library/Bee/artifacts/WinPlayerBuildProgram/ManagedStripped/ClassLayoutCollisionFixture.dll \
  Files/validation/class-layout-class-collision-01/project/Library/ScriptAssemblies/ClassLayoutCollisionFixture.dll \
  Files/validation/class-layout-class-collision-01/inspection.json
```

Choose fresh ignored directory and report names for each run. The inspector
rejects an existing report and output outside `Files/`.

After reviewing the completed receipt and inspection report, preview and apply
the guarded cleanup for this run. It retains the small receipt, logs and
inspection report while removing reproducible project and player trees:

```sh
python3 Validation/prune_generated_artifacts.py --only class-layout-class-collision-01 \
  --min-age-hours 0 --show-paths
python3 Validation/prune_generated_artifacts.py --only class-layout-class-collision-01 \
  --min-age-hours 0 --show-paths --apply
```

Run `Validation/ClassLayoutCollisionInspector/` before pruning, using the
isolated player input, stripped managed assembly and unstripped managed
assembly. Its JSON report records raw player type flags and effective sizes
separately from both managed ClassLayout declarations. It validates that
stripping retained all six declarations and each larger control increased the
native size. It compares each omitted/explicit-natural pair without using type
names or metadata tokens.

The controlled exact-target build retained declared sizes 0, 6 and 8 in both
managed inputs. The omitted and explicit six-byte cases had identical native
type flags, default-layout bits, packing, six-byte native size, instance size,
field types, attributes and offsets. The eight-byte control changed native
and instance sizes as expected. These inspected player facts cannot identify
whether the original declaration omitted `Size` or specified the natural size.
The reference-class declarations likewise retained sizes 0, 6 and 32 in both
managed inputs. The omitted and explicit six-byte classes had identical player
facts: bitfield 8336, both default-layout bits false, packing 2, native size 6,
instance size 22, and field offsets 16, 18 and 20. The 32-byte class control
had native size 32 and instance size 48, with the same field offsets. The
value-type pair had bitfield 8337, native size 6, instance size 22 and field
offsets 0, 2 and 4. Those group-specific bits and offsets distinguish a class
from a value type, but no inspected fact distinguishes omitted `Size` from an
explicit natural `Size` within either group. Neither declared value should be
inferred from player metadata alone. This establishes metadata evidence and an
original Windows x64 Release IL2CPP build; it makes no recovered-source or
class-behavior claim.
