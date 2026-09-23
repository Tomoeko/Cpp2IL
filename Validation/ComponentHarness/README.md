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

When source resolution requires both current and older framework identities,
supply both explicit sets to the round trip's `--reference-dir` options. Use
`--il-reference-dir` for the distinct target API set used by ILVerify, which
rejects duplicate assembly filenames. The receipt records these selections
separately; source and declaration resolution keep the full identity-aware set.

The recorded exact Windows 2021.3.35f1 x64 Release run passed all three selected
methods through player-only strict recovery and typed IL verification. Generated
source compiled and built successfully with IL2CPP, and the rebuilt player
matched all 24 field observations and five helper results. The stripped original,
recovered managed output and rebuilt declarations match within the independent
comparer projection: four types, three methods, seven fields, one parameter and
six custom attributes, with no declaration differences or original stripping
losses. The full source/reference identity set and distinct ILVerify set are
recorded separately. These bounded results do not establish whole-program
equivalence or original asset bindings.

The same player-only recovered source also passed the separate exact Windows
editor discovery probe: both `MonoScript.GetClass` mappings, both fresh-instance
script bindings and all six serialized field path/type/kind checks match. The
emitter's two type-to-file mappings agree with the observed script assets. All
five copied source/configuration files match the passed recovery receipt's
artifact hashes before and after the editor run.

```sh
python3 Validation/run_component_probe.py --editor "$UNITY_EDITOR" --wine "$WINE" \
  --source-dir "$COMPONENT_ROUNDTRIP/recovered/UnityProject/Assets/Recovered/ComponentFixture" \
  --recovery-receipt "$COMPONENT_ROUNDTRIP/roundtrip.json" \
  --run-dir Files/validation/component-recovery-discovery
```

The provenance option rejects incomplete receipts and missing, added or changed
copied sources. This measures fresh script bindings; original GUIDs, scenes and
serialized assets remain outside the claim.
