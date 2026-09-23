# Working on this fork

## Objective and current checkpoint

Recover accurate, readable C# from **Unity 2021.3.35f1, Windows x64, Release IL2CPP** builds, and verify that the generated code compiles in that exact Unity version. Aim for near 1:1 observable behavior and preservation of recoverable managed structure.

The user has confirmed this file and `ROADMAP.md` and authorized implementation of the full roadmap. The initial documentation gate is released. Continue routine implementation, validation and local checkpoint commits without recurring approval requests.

Read `ROADMAP.md` before choosing work. Keep its status and evidence current. Other versions and architectures are secondary; preserve existing functionality where practical without expanding the initial target.

## What counts as recovery

- Track declaration fidelity, valid managed IL, Unity source compilation, native player build success, and behavioral validation separately. Passing one does not establish the others.
- Preserve assembly/type/member identity, signatures, generics, attributes, layout, constants, dispatch, side effects, and exception behavior when the inputs establish them.
- Optimized native code does not uniquely preserve original C# syntax, comments, local names, or stripped code. Mark unavailable information and uncertainty explicitly instead of inventing evidence.
- Never count empty/default/throwing fallback bodies, unsupported instructions, guessed values, or skipped methods as successfully recovered behavior. An original, evidenced throw or default return is different from an analysis fallback.
- Incomplete output may help investigation, but must carry an explicit disposition and reasons. A strict validation path must reject unresolved behavior in its declared scope.
- Recover from the declared player inputs. Keep original source, original managed assemblies, generated C++, symbols, and analysis databases separate as validation oracles. If optional auxiliary inputs are used, disclose them and report those results separately from player-only recovery.
- Do not fix generated files by hand to make a validation run pass. Fix the general recovery or emission logic and regenerate from clean inputs.

## Public repository and local materials

- Never commit private target names, proprietary code or assets, identifying strings, local usernames, absolute private paths, credentials, license material, or private binary/evidence fingerprints. This applies to filenames, tests, snapshots, logs, commit messages, and generated reports too.
- Keep downloaded tools and task-specific downloads in the repository's gitignored `Files/` directory. Keep private inputs, generated projects, recovered source, disassembly, Binary Ninja databases, build outputs, and raw logs there or in the user-provided external locations.
- `Files/local-environment.md`, when present, maps private local resources. It is deliberately untracked; do not copy its contents into public documentation. Use neutral fixture IDs and configurable paths in tracked code.
- Verify the destination is ignored with `git check-ignore` before writing private artifacts. Never force-add ignored files. Ignore rules do not protect already tracked files; inspect the staged patch and file list before each commit.
- Public regression fixtures must be synthetic and redistributable. Derive general cases without copying private symbols, strings, bytes, or game-specific constants. Sanitize aggregate results before publishing them in tracked documents.
- Do not upload private inputs to online decompilers, issue trackers, public CI, or external services. Public research should use generic technical queries.

## Tools, references, and the exact environment

- Use the supplied Unity 2021.3.35f1 editor and matching Windows IL2CPP support installation. The local resource map contains their locations and the reference toolchain. Inventory installed versions and build settings; directory names alone do not prove a usable or matching environment.
- When running Windows Unity through Wine, use `WINEPREFIX="$HOME/.wine_unity"`, which contains the user's existing license. Preserve that prefix and its license; do not replace, reset, copy, or commit license material.
- The supplied macOS editor can provide headless source/import checks. Record the host and target for each run; a macOS editor import does not prove that a Windows x64 IL2CPP player builds or runs.
- Treat the supplied editor variants as the build authority. Do not compare the privately supplied Unity executables against original/public executables, or substitute another installation without the user's direction.
- The older recovery tool is an incomplete reference, not a correctness oracle. Reuse general ideas only after independent verification; check licensing and provenance before importing any code.
- Prefer the available Binary Ninja skill for substantive native-code investigations, and read its instructions when using it. Corroborate decompiler hypotheses with metadata, native instructions, matching runtime definitions, and controlled builds. Keep private analysis artifacts local.
- Use official versioned Unity documentation and primary technical sources for research. Verify version-specific assumptions against the supplied installation and small reproducible builds.

## Repository map and implementation approach

| Area | Responsibility |
| --- | --- |
| `LibCpp2IL/` | Binary and metadata parsing, registration, runtime structures |
| `Cpp2IL.Core/InstructionSets/` | Native instruction decoding/lifting |
| `Cpp2IL.Core/ISIL/`, `Graphs/`, `Analysis/` | Intermediate representation, control flow, type/data-flow and IL2CPP recovery |
| `Cpp2IL.Core/Model/Contexts/` | Managed application, type, and method models |
| `Cpp2IL.Core/IlGenerator.cs` | Managed IL generation |
| `Cpp2IL.Core/OutputFormats/` | DLL and diagnostic/source output formats |
| `Cpp2IL/` | CLI orchestration |
| `Cpp2IL.Core.Tests/`, `LibCpp2ILTests/` | Existing NUnit and xUnit tests |

- Extend the existing pipeline where it is sound. Centralize version/layout and ABI knowledge instead of duplicating magic offsets or pattern rules across emitters.
- Follow `.editorconfig` and nearby conventions. Keep changes cohesive; deduplicate and organize touched code when it improves correctness or clarity. Avoid unrelated rewrites and repository-wide formatting churn.
- Resolve uncertainty at the stage that owns it. Preserve provenance through transformations so a source-emission failure can be traced to metadata, lifting, analysis, or output.
- Keep tool implementation language/framework requirements separate from generated Unity code. The current tool uses modern .NET; emitted source must use the supplied Unity compiler's language features, API profile, and assembly references.
- Unity 2021.3 documents C# 9 with restrictions. Use compatible syntax, including block namespaces, and verify the exact editor rather than assuming desktop Roslyn success is sufficient. See the [Unity 2021.3 C# compiler reference](https://docs.unity3d.com/2021.3/Documentation/Manual/CSharpCompiler.html).
- Do not regenerate Unity/framework reference assemblies as application source or resolve missing dependencies using arbitrary newer assemblies. Preserve application assembly boundaries and report missing references.
- Prefer deterministic output ordering and stable neutral diagnostic identifiers. Measure memory/runtime before optimizing, and preserve correctness evidence when changing analysis performance.

## Validation discipline

The project files and CI use .NET 10. `global.json` pins the validated SDK baseline to 10.0.107 with patch roll-forward and selects Microsoft Testing Platform. Record the actual SDK/package baseline when changing dependencies. The README's older .NET version references are not the authority.

Existing repository-wide commands, to run from the repository root when appropriate:

```sh
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

These are repository build/test commands, not proof that recovered code compiles in Unity. The full legacy test suite can download external samples; inspect its fixture setup before running it and keep new downloads/private fixtures under `Files/`. Prefer focused checks for the changed area, then run the required broader checks once.

- Add unit tests only for a meaningful regression, algorithmic edge case, or invariant that needs protection. Do not add tests for documentation-only edits or tests that merely repeat the implementation.
- For recovery changes, prioritize small synthetic fixtures built with the exact target. Compare emitted declarations and behavior with known source, then compile regenerated source in Unity and build/run the relevant native fixture.
- Test Release semantics, including integer width/signedness, floating-point edge cases, side effects, aliasing, exceptions, value types, generics, and runtime dispatch as each feature is implemented.
- Record commit, tool versions, target settings, input set, command, exit status, diagnostics, coverage denominator, and result. Keep private raw evidence in `Files/`; only neutral summaries belong in the repository.
- A zero exit status alone is insufficient. Require fresh output, completed compilation/build evidence, and an explicit result for each validation stage. Report missing tools or unavailable checks as unverified, never as passed.

## Git workflow and reporting

- Work only in this local fork. Confirm the current branch, working-tree changes, and remote roles before making a checkpoint. Preserve unrelated user changes.
- Make small local checkpoint commits after coherent, reviewed changes. Stage explicit files and inspect `git diff --cached` for scope and private information. Use the configured public-safe author identity; do not introduce a personal address into commit metadata.
- **Never push**, force-push, publish releases/packages, or write changes to the upstream repository. The user alone reviews and pushes this fork. Do not invoke release scripts or remote CI workflows as a substitute for local validation.
- Do not rewrite existing commits or perform destructive Git cleanup unless requested. If a new branch is useful, use the `codex/` prefix unless the user specifies another name.
- Report what changed, which checks actually ran, what remains unsupported, and the local commit ID. Keep code compilation claims separate from recovered-code validation claims.
