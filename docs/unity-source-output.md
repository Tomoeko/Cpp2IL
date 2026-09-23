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

Multiple explicitly supplied framework versions can coexist when the target engine and application require different identities. Each reference must match exactly one candidate's name, version, culture and public-key token. Duplicate exact matches fail; directory order never chooses an API version or creates a redirect.

The exporter writes these independent artifacts:

- `source-recovery-report.json`: method dispositions for the recovery pass, including unsupported behavior and fallback reasons.
- `UnityProject/source-emission-report.json`: source generation and decompiler diagnostics, selected assembly boundaries, and external references. Unity compilation, native rebuilding, script bindings, and behavior stay explicitly unverified.
- `UnityProject/Assets/Recovered`: generated application C# and assembly definitions. `Assembly-CSharp` uses Unity's predefined assembly; `Assembly-CSharp-firstpass` is placed beneath `Assets/Plugins`. Custom assembly definitions cannot reference predefined assemblies, so those unsupported dependency graphs fail explicitly.
- `UnityProject/RecoveredManaged`: intermediate recovered assemblies outside `Assets`, so Unity cannot accidentally compile against duplicate original/generated types.
- `UnityProject/ProjectSettings/ProjectVersion.txt`: the required editor version. This file does not configure or prove the original player's API compatibility, stripping, defines, or native Release settings.

The generated project requires an empty destination. Existing output is never silently reused or erased. Default output may contain fallback bodies and is reported as partial when recovery gaps or decompiler warnings exist. `--strict-recovery` rejects detected gaps within the selected application assemblies before source generation, and rejects decompiler warnings. Passing strict checks is not a managed type verifier, an exact-editor compilation check, or behavioral equivalence evidence.

Framework and Unity assemblies are never regenerated as source. External references are recorded but not copied into `Assets`. For non-framework dependencies, supply the matching licensed/redistributable plugin or package through the validation harness. The minimal package manifest intentionally does not invent package names or versions from assembly names. Optional Unity engine modules, package assembly definitions, platform defines, API compatibility, and editor/player build settings require explicit configuration. Unknown project settings remain unknown.

Each assembly retains `Recovered.cs` for assembly/module attributes and ordinary types. Eligible top-level, nongeneric MonoBehaviour and ScriptableObject types are emitted into separate class-matching files. Namespace and assembly directories avoid Unity's special-folder rules. Unsafe, unrepresentable or colliding component paths produce diagnostics. The report's `SourceFiles` lists all files and `ComponentScripts` maps component identities to files; the existing `SourceFile` continues to identify the central file.

The [component discovery probe](../Validation/ComponentFixture/README.md) passes exact Windows editor import for two scripts and six serialized field identities using an authored managed oracle. That result validates the emitter independently; it does not establish player-only component body recovery. Original script GUIDs, scene bindings and assets remain unreconstructed. Generated files must not be repaired manually to pass a check. Fix recovery/emission logic and regenerate cleanly.

Public emitter tests use synthetic managed IL and explicit test-runtime references to protect output syntax, body preservation, assembly layout, reference isolation, and fresh-output rules. They do **not** claim Unity or Windows IL2CPP validation. The separate exact-editor fixture/harness must establish those gates.

Primary references: [Unity 2021.3 C# compiler restrictions](https://docs.unity3d.com/2021.3/Documentation/Manual/CSharpCompiler.html), [ILSpy 9.1 settings](https://github.com/icsharpcode/ILSpy/blob/v9.1/ICSharpCode.Decompiler/DecompilerSettings.cs), and [ILSpy 9.1 C# decompiler](https://github.com/icsharpcode/ILSpy/blob/v9.1/ICSharpCode.Decompiler/CSharp/CSharpDecompiler.cs).
