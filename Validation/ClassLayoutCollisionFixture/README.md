# Declared versus effective class size

This synthetic fixture holds field signatures and pack constant while varying
only the declared `StructLayout.Size`: omitted, six bytes (the natural size),
and eight bytes. The three byte/short/byte fields have offsets 0, 2 and 4
under pack 2, with a six-byte natural size. The larger declaration is a control
that checks whether the native size path can retain an actual size increase.

Stage this directory beside `Validation/Fixture/` inside a new ignored source
directory. Build it through `Validation/run_fixture.py` with the supplied
Unity 2021.3.35f1 Windows editor, the existing Wine prefix and the Windows x64
Release IL2CPP profile. Use `--stage build`; the arithmetic fixture supplies
the runner's editor behavior check. Keep the project and player under `Files/`.

```sh
python3 - <<'PY'
from pathlib import Path
import shutil
source = Path("Files/validation/class-layout-collision-source")
shutil.copytree("Validation/Fixture", source)
shutil.copytree("Validation/ClassLayoutCollisionFixture", source / "ClassLayoutCollision")
PY

python3 Validation/run_fixture.py --editor "$UNITY_WINDOWS_EDITOR" --wine "$WINE" \
  --toolchain-root "$WINDOWS_TOOLCHAIN" \
  --source-dir Files/validation/class-layout-collision-source \
  --run-dir Files/validation/class-layout-collision-01 --stage build --timeout 1200

dotnet build Validation/ClassLayoutCollisionInspector/ClassLayoutCollisionInspector.csproj -c Release
dotnet Validation/ClassLayoutCollisionInspector/bin/Release/net10.0/ClassLayoutCollisionInspector.dll \
  Files/validation/class-layout-collision-01/player-input \
  Files/validation/class-layout-collision-01/project/Library/Bee/artifacts/WinPlayerBuildProgram/ManagedStripped/ClassLayoutCollisionFixture.dll \
  Files/validation/class-layout-collision-01/project/Library/ScriptAssemblies/ClassLayoutCollisionFixture.dll \
  Files/validation/class-layout-collision-01/inspection.json
```

Choose fresh ignored directory and report names for each run. The inspector
rejects an existing report and output outside `Files/`.

After reviewing the completed receipt and inspection report, preview and apply
the guarded cleanup for this run. It retains the small receipt, logs and
inspection report while removing reproducible project and player trees:

```sh
python3 Validation/prune_generated_artifacts.py --only class-layout-collision-01 \
  --min-age-hours 0 --show-paths
python3 Validation/prune_generated_artifacts.py --only class-layout-collision-01 \
  --min-age-hours 0 --show-paths --apply
```

Run `Validation/ClassLayoutCollisionInspector/` against the resulting isolated
player input, stripped managed assembly and unstripped managed assembly. Its
JSON report records raw player type flags and effective sizes separately from
both managed ClassLayout declarations. It validates that stripping retained
the three declared sizes and the control has a larger native size, then compares
the omitted and explicit natural-size records without using type names or
metadata tokens.

The controlled exact-target build retained declared sizes 0, 6 and 8 in both
managed inputs. The omitted and explicit six-byte cases had identical native
type flags, default-layout bits, packing, six-byte native size, instance size,
field types, attributes and offsets. The eight-byte control changed native
and instance sizes as expected. These inspected player facts cannot identify
whether the original declaration omitted `Size` or specified the natural size.
Neither value should be counted as recovered declared size. This establishes
metadata evidence only; it makes no recovered-source, Unity compilation or
behavioral claim for these structs.
