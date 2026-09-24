# Unity C# source output

`cs_unity` is an initial code-only project exporter for Unity **2021.3.35f1 Windows x64** inputs. It generates source from the existing recovered CIL model with the MIT-licensed [ICSharpCode.Decompiler 9.1.0.7988](https://www.nuget.org/packages/ICSharpCode.Decompiler/9.1.0.7988). This dependency supports the tool's .NET Standard 2.0 target. Its language settings disable file-scoped namespaces and Unity's unsupported C# features, including init setters and covariant returns. The target editor remains the compiler authority; decompiler settings alone do not establish that an arbitrary assembly compiles.

Specify application assemblies explicitly, using their exact metadata names without `.dll`. Supply directories containing the matching Unity/API reference assemblies. Paths below are placeholders; keep actual input paths and generated results local and ignored.

```sh
dotnet run --project Cpp2IL -c Release -f net10.0 --no-restore -- \
  --force-binary-path "$PLAYER_BINARY" \
  --force-metadata-path "$PLAYER_METADATA" \
  --force-unity-version 2021.3.35f1 \
  --output-as cs_unity \
  --unity-source-assemblies Assembly-CSharp,Synthetic.Library \
  --unity-reference-dir "$TARGET_REFERENCE_DIRECTORY" \
  --strict-recovery \
  --output-to Files/source-run
```

Use the CLI's `--help` for platform-specific input discovery options. `--unity-reference-dir` accepts multiple directory arguments. The resolver searches only explicitly configured directories and recovered selected application assemblies. Missing, ambiguous, and mismatched assembly identities fail; the host .NET runtime, GAC, working directory, and NuGet cache are never automatic reference sources. Do not provide original managed application assemblies to a player-only recovery run. Deliberate auxiliary references must be recorded separately in validation evidence.

To supply package dependencies, add `--unity-package-manifest "$LOCAL_MANIFEST_JSON"` to the `cs_unity` command. This is an **explicit auxiliary input**, separate from the player binary and metadata. The exporter validates and copies its bytes to `UnityProject/Packages/manifest.json`; without the option, it keeps the empty `{"dependencies":{}}` manifest. No package identity or version is inferred from player metadata. The source-emission report records only whether the manifest was explicit and the direct dependency count, without recording its path, package names, URLs, or fingerprint. Keep supplied manifests and generated projects in ignored local storage when they contain private information.

The accepted Unity 2021.3 project-manifest fields are `dependencies`, `scopedRegistries`, `testables`, `registry`, `enableLockFile`, and `resolutionStrategy`. Dependencies may use Semantic Versioning registry versions or remote HTTPS Git URLs (`.git` suffix or `git+https://` prefix). Unity's Git `?path=/subfolder` syntax is accepted only for plain subdirectories relative to the repository root, optionally followed by a revision; encoded, traversing, or multi-parameter paths fail. Local `file:` dependencies, host filesystem paths, URL user info, loopback registries, empty manifests, duplicate keys, malformed JSON, and unsupported fields fail before a project is written. URL paths and revision names are opaque, so review an explicit manifest for private information before sharing its generated project. Remote package availability and package-to-assembly compatibility still require an exact Unity import/build check. The validator uses System.Text.Json 10.0.0 for the tool's .NET Standard 2.0 and .NET 10 targets; this does not change generated Unity C# syntax.

The player does not record whether an external managed assembly came from an assembly definition, a precompiled plug-in or a target-provided reference. Supply an explicit map when selected source depends on such an assembly:

```json
{
  "references": [
    { "assembly": "Synthetic.Package.Api", "kind": "asmdef" },
    { "assembly": "Synthetic.Plugin.Api", "kind": "precompiled-plugin" }
  ]
}
```

Pass the file with `--unity-external-reference-map "$LOCAL_REFERENCE_MAP_JSON"`. An `asmdef` entry goes into the generated assembly definition's `references`; a `precompiled-plugin` entry goes into `precompiledReferences` as `<assembly>.dll` with `overrideReferences` enabled. A controlled exact-target run compiled a plug-in whose filename differed from its managed assembly name in the editor, but its Windows IL2CPP build failed during Unity's assembly resolution. That alternate filename layout is outside this verified source-output path. `target-provided` is available for an explicitly confirmed editor/runtime assembly. For `Assembly-CSharp` or `Assembly-CSharp-firstpass`, an external `asmdef` or plug-in entry also needs `"autoReferenced": true` because predefined assemblies have no generated assembly definition. The map is an explicit auxiliary input and records classification, not the dependency's bytes, package version, platform compatibility or availability. Unclassified external references produce `SOURCE007` and partial source output, which strict mode rejects. Keep private maps and dependencies in ignored local storage.

For an explicit-manifest round trip, the validation harness requires Unity to produce `Packages/packages-lock.json` and compares its resolved-file hash between the original and recovered fresh projects. A manifest that disables lock-file generation cannot pass that package-resolution gate. The neutral external-reference fixture also tests an embedded package assembly definition and precompiled plug-in in fresh exact-editor projects, with their managed DLLs disclosed as synthetic auxiliary recovery references.

Multiple explicitly supplied framework versions can coexist when the target engine and application require different identities. Each reference must match exactly one candidate's name, version, culture and public-key token. Duplicate exact matches fail; directory order never chooses an API version or creates a redirect.

The exporter writes these independent artifacts:

- `source-recovery-report.json`: method dispositions for the recovery pass, including unsupported behavior and fallback reasons.
- `UnityProject/source-emission-report.json`: source generation and decompiler diagnostics, selected assembly boundaries, and external references. Unity compilation, native rebuilding, script bindings, and behavior stay explicitly unverified.
- `UnityProject/Assets/Recovered`: generated application C# and assembly definitions. `Assembly-CSharp` uses Unity's predefined assembly; `Assembly-CSharp-firstpass` is placed beneath `Assets/Plugins`. Custom assembly definitions cannot reference predefined assemblies, so those unsupported dependency graphs fail explicitly.
- `UnityProject/RecoveredManaged`: intermediate recovered assemblies outside `Assets`, so Unity cannot accidentally compile against duplicate original/generated types.
- `UnityProject/ProjectSettings/ProjectVersion.txt`: the required editor version. This file does not configure or prove the original player's API compatibility, stripping, defines, or native Release settings.
- `UnityProject/Packages/manifest.json`: the empty default or the separately supplied, validated package manifest. The file's content comes from the user, not the player.

The generated project requires an empty destination. Existing output is never silently reused or erased. Default output may contain fallback bodies and is reported as partial when recovery gaps or decompiler warnings exist. `--strict-recovery` rejects detected gaps within the selected application assemblies before source generation, and rejects decompiler warnings. Passing strict checks is not a managed type verifier, an exact-editor compilation check, or behavioral equivalence evidence.

Known framework and Unity engine assemblies are not regenerated as source. External references are recorded but not copied into `Assets`. Supply matching licensed or redistributable plug-ins and packages to the fresh validation project. The reference-kind map does not establish their full managed identities or Unity importer settings. Platform defines, API compatibility, and editor/player build settings still require explicit configuration. Unknown project settings remain unknown.

Each assembly retains `Recovered.cs` for assembly/module attributes and ordinary types. Eligible top-level, nongeneric MonoBehaviour and ScriptableObject types are emitted into separate class-matching files. Namespace and assembly directories avoid Unity's special-folder rules. Unsafe, unrepresentable or colliding component paths produce diagnostics. The report's `SourceFiles` lists all files and `ComponentScripts` maps component identities to files; the existing `SourceFile` continues to identify the central file.

The [component discovery probe](../Validation/ComponentFixture/README.md) passes exact Windows editor import for two scripts and six serialized field identities using an authored managed oracle. That result validates the emitter independently; it does not establish player-only component body recovery. Original script GUIDs, scene bindings and assets remain unreconstructed. Generated files must not be repaired manually to pass a check. Fix recovery/emission logic and regenerate cleanly.

Public emitter tests use synthetic managed IL and explicit test-runtime references to protect output syntax, body preservation, assembly layout, reference isolation, and fresh-output rules. They do **not** claim Unity or Windows IL2CPP validation. The separate exact-editor fixture/harness must establish those gates.

Primary references: [Unity 2021.3 C# compiler restrictions](https://docs.unity3d.com/2021.3/Documentation/Manual/CSharpCompiler.html), [Unity 2021.3 assembly definition format](https://docs.unity3d.com/2021.3/Documentation/Manual/AssemblyDefinitionFileFormat.html), [Unity 2021.3 embedded packages](https://docs.unity3d.com/2021.3/Documentation/Manual/upm-embed.html), [Unity 2021.3 project manifest](https://docs.unity3d.com/2021.3/Documentation/Manual/upm-manifestPrj.html), [Unity 2021.3 lock files](https://docs.unity3d.com/2021.3/Documentation/Manual/upm-conflicts-auto.html), [Unity 2021.3 Git dependency syntax](https://docs.unity3d.com/2021.3/Documentation/Manual/upm-git.html), [ILSpy 9.1 settings](https://github.com/icsharpcode/ILSpy/blob/v9.1/ICSharpCode.Decompiler/DecompilerSettings.cs), and [ILSpy 9.1 C# decompiler](https://github.com/icsharpcode/ILSpy/blob/v9.1/ICSharpCode.Decompiler/CSharp/CSharpDecompiler.cs).
