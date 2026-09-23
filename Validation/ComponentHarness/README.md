# Component player recovery profile

The `components` profile builds the unchanged `ComponentFixture` and an independent
runtime driver. The selected assembly has three managed bodies: the two implicit
component constructors and `OrdinaryHelper.Identity`. The driver remains outside
that recovery scope.

For each of two fresh `GameObject`/`ScriptableObject` pairs, the driver reads six
field paths before and after assignment. These 24 observations preserve class,
assembly, field path, declared managed type and value. Private strings use runtime
reflection; a missing field or null intermediate object fails the run. Null and
empty strings differ, and the assigned object reference must be the same asset.
Five signed boundary inputs also exercise the identity helper. These checks do not
establish original serialized asset, GUID or scene restoration.

The driver's `link.xml` deliberately preserves the selected synthetic assembly for
reflection and a stable method denominator. This configuration is copied and
hashed with the harness. Record the actual stripped and unstripped method counts
after building; preservation configuration alone does not prove no stripping.

```sh
python3 Validation/run_fixture.py --profile components --editor "$UNITY_EDITOR" \
  --wine "$WINE" --toolchain-root "$UNITY_TOOLCHAIN" --stage run \
  --run-dir Files/validation/components-original
```

Use the resulting isolated `player-input` directory for recovery. The original
managed assembly is a declaration oracle only, and enters the comparison stage
after recovery. This native profile is separate from the managed-oracle source
layout experiment described in `ComponentFixture/README.md`.

Until a recorded run completes, native recovery, typed IL verification, generated
source compilation and rebuilt native behavior remain unverified independently.
