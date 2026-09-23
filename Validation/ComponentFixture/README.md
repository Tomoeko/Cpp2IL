# Component source discovery fixture

This fixture separates compilable C# from Unity script discovery. It contains a
`MonoBehaviour`, a `ScriptableObject`, six serialized field paths (including a
private field, an object reference and a nested value field), an ordinary helper
and an assembly attribute.

Run the fresh-project editor probe with the supplied exact editor:

```sh
python3 Validation/run_component_probe.py --editor "$UNITY_EDITOR" \
  --run-dir Files/validation/component-authored
```

For the Windows editor on another host, also provide `--wine "$WINE"`. The probe
uses the existing licensed prefix and explicitly selects `NET_Unity_4_8` before
the measured import. It checks `MonoScript.GetClass`, the new instances' `m_Script`
references, and serialized paths, property kinds and declared managed field types.
It checks that ordinary types and assembly attributes survive too.

The emitter-only experiment deliberately consumes that authored managed assembly
as an **oracle**, not player inputs. Build the small driver against a saved tool
directory, then supply the original managed assembly, a fresh output directory
and every explicit reference directory:

```sh
dotnet build Validation/ComponentEmitter/ComponentEmitter.csproj -c Release \
  -p:Cpp2ILToolDirectory="$CPP2IL_TOOL" -p:RestorePackagesPath="$PWD/Files/nuget" \
  -o Files/validation/component-emitter
dotnet Files/validation/component-emitter/ComponentEmitter.dll \
  "$COMPONENT_ORACLE_DLL" Files/validation/component-emitted \
  "$UNITY_REFERENCES" "$UNITY_LEGACY_REFERENCES" "$UNITY_ENGINE_REFERENCES"
python3 Validation/run_component_probe.py --editor "$UNITY_EDITOR" \
  --source-dir Files/validation/component-emitted/Assets/Recovered/ComponentFixture \
  --run-dir Files/validation/component-imported
```

The supplied engine assemblies can reference older framework identities than the
application. Provide the matching installed reference sets explicitly. Resolution
requires one exact identity match; it neither chooses a version by directory
order nor invents framework redirects. The driver records its original-assembly
provenance, tool hash and reference directories with the emitted project.

The measured single-file baseline compiles in Unity 2021.3.35f1 but discovers
neither component: `Recovered.cs` returns null from `GetClass`. The ordinary type,
assembly attribute and serialized fields still exist, so compilation alone would
miss this defect. `--expect-discovery observe` records such a baseline without
declaring discovery passed.

The corrected output passes a fresh import in the supplied Windows Unity
2021.3.35f1 editor with `NET_Unity_4_8`: both component scripts are discovered,
both fresh instances bind to their matching script assets, and all six serialized
field paths retain their declared managed types and property kinds. The ordinary
helper and assembly attribute also survive. This result validates source layout
using the authored managed assembly as an oracle; native recovery is outside this
probe's scope.

Generated component files use their exact class names beneath namespace folders
prefixed with `ns-`, so a namespace such as `Editor` cannot select Unity's special
folder behavior. Assembly-derived directories escape special/hidden folder names
while keeping their original assembly-definition identities; intentional firstpass
placement remains separate.
Assembly/module attributes and other types stay in `Recovered.cs`. Reports retain
`SourceFile` for the central file and add `SourceFiles` for the complete assembly,
plus `ComponentScripts` type-to-file mappings. Consumers should use `SourceFiles`
when reading all generated code. Nested/generic components and unsafe or colliding
portable paths remain explicit diagnostics. Namespaces ending in Unity's ignored
`~` suffix are rejected. Component names and namespace segments must preserve
their metadata identities in C#: malformed identifiers, formatting characters
that the compiler removes, and currently unverified supplementary Unicode
characters are diagnosed instead of receiving a script mapping.

These checks concern newly imported scripts and fresh instances only. The probe
does not copy `.meta` files, reconstruct original GUIDs, restore scenes or assets,
run player-only body recovery, or establish native behavior. Generated reports
keep editor discovery unverified until the separate editor probe runs.

Primary API references: [MonoScript.GetClass](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/MonoScript.GetClass.html)
and [Unity script creation and use](https://docs.unity3d.com/2021.3/Documentation/Manual/CreatingAndUsingScripts.html).
The import rules are documented under [special folder names](https://docs.unity3d.com/2021.3/Documentation/Manual/SpecialFolders.html)
and [script compilation order](https://docs.unity3d.com/2021.3/Documentation/Manual/ScriptCompileOrderFolders.html).
