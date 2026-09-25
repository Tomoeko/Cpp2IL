# Unity 2021.3.35f1 recovery roadmap

## Target and limits

Recover readable, verifiably compilable C# from **Unity 2021.3.35f1 Windows x64 Release IL2CPP** player binaries and metadata. The goal is 1:1 managed-structure and behavioral fidelity where the inputs establish it. A successful fixture is a bounded result, not proof that an entire application is recovered.

Windows x64 is the initial target. Other Unity versions, architectures, obfuscation variants, assets and scenes are later work. Preserve existing support where practical. Original source text, comments, local names, optimized-away code and some declaration facts cannot be inferred uniquely from a stripped player. Report those gaps; do not invent them.

Recovery must use the declared player inputs. Original source, original managed assemblies, generated C++, native symbols and analysis databases are validation oracles only. If a dependency is supplied as an auxiliary input, disclose it and report that result separately from player-only recovery. Keep all private inputs, raw logs, tools and generated projects in ignored `Files/`; tracked fixtures and documentation must be neutral.

The supplied editor variants are the authority for this target. Do not substitute a different version or compare the privately supplied executables against public originals. A macOS editor import check is useful but does not prove a Windows x64 IL2CPP build.

## Acceptance gates

Record each gate for an explicit selection of assemblies and methods. Passing one gate does not imply another.

| Gate | Required result |
| --- | --- |
| Input profile | Authenticate Unity version, PE x64 binary, metadata and registration; record known and unknown build settings. Do not infer `Release` from a filename. |
| Declaration fidelity | Compare assembly, type and member facts against a known oracle when available; also report facts unavailable from the player. A zero-difference oracle projection is not player-derived certainty. |
| Recovery and typed IL | Report every selected method as emitted, no managed body, or unresolved with reasons. Strict mode rejects partial, skipped, fallback, guessed and unsupported behavior. Verify emitted IL, references, stack, control flow and exception regions. |
| Unity source compilation | Regenerate clean C# without hand edits, then compile it in the supplied Windows Unity 2021.3.35f1 editor with the intended references and symbols. |
| Native rebuild | Produce a fresh nondevelopment Windows x64 IL2CPP player with native C++ compiler configuration `Release`; require a zero-error Unity BuildReport and actual output artifacts. |
| Behavior | Compare original and recovered editor/player observations, including returns, state changes, dispatch, initialization, null and bounds failures and exceptions relevant to the feature. State the observation count and limits. |
| Unity integration | When claimed, verify component identity, serialized fields, script references and asset bindings separately. |

A `dotnet build -c Release` validates this tool, not recovered Unity source. A zero process exit, emitted C#, or a method labeled `Emitted` is not enough to pass the later gates. Count generic/shared-address methods by distinct managed identity. Preserve effect order and reject uncertain native call identity, helper behavior or omitted exits.

## Current evidence

The latest recorded bounded total is **361 selected methods across 71 exact-target fixture round trips**. This total includes distinct controls of some shapes and explicitly disclosed auxiliary-assisted cases; it is not a unique-method count or a broad recovery percentage. At least one selected interface method correctly has no managed body. The latest complete player-only round trip uses an eight-method iterator control with Win64 `Release` IL2CPP `OptimizeSize`: 8/8 strict emission, typed IL verification, zero declaration-projection differences against its stripped oracle before and after rebuilding, Windows editor compilation, native rebuild, and 13 matching observations in each original/recovered editor/player stage. The earlier ten-method array-call control established a direct instance call, a separate returned `int[]`, ordered receiver and returned-array null failures, unsigned bounds failure, and a caller-visible counter effect with 30 matching observations in each stage. Neither fixture establishes arbitrary iterator or call-result array recovery. The parameter-array comparison control separately established two distinct parameter-origin reads with an intervening observable call and 112 matching observations in each stage.

The current paired player-only audit preserves every ordered full-input and selected managed identity. The committed call-result array proof produced zero disposition and first-reason transitions in either scope:

| Scope | Full input | Selected | Emitted | Failed | Partial | No managed body | Unresolved selected |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Private application | 38,700 | 7,256 | 1,621 | 4,616 | 2 | 1,017 | 4,618 |
| Independent application | 91,198 | 12,724 | 4,343 | 7,822 | 80 | 479 | 7,902 |

Both broad strict commands exit 1. These are analysis dispositions, not verified 1:1 behaviors. Neither selected application has whole-scope typed IL, Unity source compilation, native rebuild or behavioral equivalence. Their original native `Release` configuration is unverified. A separate independent original-project diagnostic produced Unity integration errors and is not a clean acceptance baseline.

The latest recorded Release solution build with .NET SDK 10.0.107 had zero errors and four NuGet packaging warnings (NU5104). Its offline test run discovered 1,645 cases: 1,517 passed, 128 optional-input checks skipped and none failed. Skips are not passing fixture evidence. Raw receipts remain local and ignored. The reviewed earlier runs and original baselines have had generated projects and players pruned; a future baseline reuse needs a fresh original build.

A controlled code-generation check now records `OptimizeSpeed` or `OptimizeSize`
explicitly and rejects a baseline with a different setting. All 28 retained
exact-target fixture player inputs inspected before this check had the proven
`0x5d` metadata-helper first span. A fresh Win64 `Release` IL2CPP `OptimizeSize`
iterator control retained that form but put its once flag in writable,
file-backed zero data. The iterator proof now checks that byte and excludes
loader relocation before accepting it. The eight-method round trip passed all
declared gates above. The alternate `0x37` metadata helper in broader inputs
remains unproved, and those inputs' original native build settings remain
unverified.

## Milestone 0 — Reproducible baseline and honest reporting

**Status: complete for the controlled baseline; maintained continuously.** The repository builds on the pinned .NET 10 SDK baseline. The CLI retains every input method in a recovery report, gives exclusions and failures explicit reasons, and rejects detected incomplete recovery in strict mode. The validation harness separates original/recovered editor compilation, typed IL, declarations, native builds and behavior. It checks fresh outputs and does not accept empty behavior reports or stale artifacts.

**Exit criterion:** reproducible tool and target settings, fixed denominators, no fallback or unsupported method counted as recovered, and optional local checks reported as skipped when their inputs are absent. Keep this reporting invariant as later stages change.

## Milestone 1 — One complete recovery path

**Status: complete for its initial four-method slice; expanded through bounded fixtures.** The `cs_unity` exporter emits C# from recovered IL using pinned decompiler and explicit target references. Its initial arithmetic fixture recovered four methods from isolated player inputs, passed typed IL, compiled in the exact Windows editor, rebuilt a Release IL2CPP player and matched 81 original/recovered input pairs. Later controls exercise more shapes; the current bounded total is above.

**Exit criterion:** at least one complete, clean player-only path through all gates with no manual repair of generated files. This milestone is met for bounded controls; it does not certify a full application.

## Milestone 2 — Declaration fidelity

**Status: partial.** The comparer preserves assembly/type/member identities and exposes raw differences. An independent managed-backup comparison has zero differences over 1,769 types and 12,724 methods, but that is an oracle projection. Version-29 player metadata still leaves some authored declaration facts unknown. Strict source output rejects blocking unknowns rather than deriving them from an effective native value.

Known gaps include authored `StructLayout.Size` when omitted and explicit natural size have the same player representation, some marshaling descriptors with identical native evidence, method-linked return-parameter rows/attributes, `ref` versus `ref readonly` returns, and original serialized `System.Type` name qualification. Attribute provenance checks can establish retained type identity without recovering its original spelling. The current exporter also cannot reconstruct original script GUIDs or missing assembly/package references from code-only input.

**Exit criterion:** all representable facts in the declared scope compare correctly, every unidentifiable fact is marked unknown, and generated application assemblies compile with the supplied Unity API profile. A zero-difference comparison to a stripped oracle remains labeled as such; it cannot clear a player-only unknown.

## Milestone 3 — Windows x64 Release semantics

**Status: partial.** Exact-target controls now cover bounded arithmetic, integer widths and shifts, floating comparisons, field and array reads/writes, reference barriers and null/bounds failures, direct calls, selected virtual/generic dispatch, constructor chains, static initialization, iterators, exception exits and composed operations. Complete-body proofs authenticate native bytes, unwind boundaries, layouts, aliases, helpers and effect order before they change the typed graph. Unsupported variants remain unresolved.

The six-method composed control authenticates a reference-field store after an observable marker increment and source read; it preserves that order and the destination null failure. The newer parameter-array control authenticates two distinct object-array parameters, a shared index offset, separate null and bounds guards, an intervening direct call, reference equality, and five final managed operations in emitted block order. Binary Ninja inspection corroborated the native read and call sequence in the neutral player; the binary was closed without saving analysis changes. These controls do not establish arbitrary field stores, array sequences or concurrent behavior. Native optimization may erase original managed call boundaries and source ordering even when observed behavior matches.

The array-call extension authenticates a direct nonvirtual call whose result is an `int[]`, with a separate argument array, a preserved signed index, a receiver null guard, a returned-array null guard, an unsigned bounds check, and nonreturning helper exits. The complete file-backed unwind region and unique managed callee bind the shape before emitting a managed call followed by `ldelem.i4`. Binary Ninja inspection corroborated the native sequence; the binary was closed without saving analysis changes.

The `OptimizeSize` iterator control authenticates an alternate once-flag
storage location. Its PE relocation-directory proof rejects any file-backed
zero byte that the loader might alter, and rejects malformed or unsupported
relocation records for this inference. The accepted result remains a bounded
factory shape; other iterator layouts and the alternate metadata helper fail
strictly.

**Exit criterion:** each admitted shape has a reproducible exact-target source/native pair, meaningful positive and negative evidence, valid emitted IL, and matching bounded behavior. Generalize shared ABI, helper and type rules only when the broader evidence supports them; keep all other shapes strict failures.

## Milestone 4 — Unity project and player verification

**Status: partial.** The controlled fixture harness builds fresh projects in the supplied Windows editor, selects Win64 IL2CPP with native `Release`, verifies declarations before and after rebuilding, and runs original/recovered editor and player observations. Auxiliary-assisted fixtures are labeled separately. A source-only recheck or macOS import does not replace a fresh Windows native-build result.

Project-wide dependency closure, assembly definitions, packages, platform defines, component identity and serialized asset bindings remain incomplete for broad inputs. Do not fabricate engine/framework source or edit generated files to force a build.

**Exit criterion:** for each claimed project scope, clean player-only regeneration, typed IL, declaration comparison, exact Windows source compilation, zero-error Release native rebuild and recorded original/recovered behavior all pass. Report unavailable dependencies or unverified integration separately.

## Milestone 5 — Independent integration and maintainable coverage

**Status: incomplete.** The two broad selected scopes above still have 4,618/7,256 and 7,902/12,724 unresolved methods. The paired audit detects no broad gain from the call-result array proof. Leading first-reason families in the two selected scopes are runtime null guards (1,516/3,847), terminal bounds helpers (559/536), missing decoded boundaries (249/662), unproved exits (141/640), and ambiguous callsites (84/265). First reasons are ordered blockers, not independent root-cause counts; clearing one may reveal another. Broad `Emitted` transitions are never counted as validated behavior without the later gates.

**Exit criterion:** repeatable improvement on controlled and independent inputs, explainable remaining gaps, valid compilation/rebuild/behavior for every claimed scope, and no private information in tracked changes. A claim of complete 1:1 recovery requires zero unresolved behavior and the applicable fidelity gates for that full declared scope. If player-only information is irrecoverable, report a bounded result instead.

## Next work

1. Recount current first-failure families on authenticated broad reports, preserving the same full and selected identities. Prioritize actionable shared null-guard, terminal-bounds, missing-boundary, unproved-exit and ambiguous-callsite cohorts. Do not promote sampled native shapes to recovered methods.
2. Build a small neutral exact-target Release fixture for one closed candidate. Inspect native bytes, metadata, runtime definitions and control flow; preserve effect order and exceptional exits. Use Binary Ninja where native evidence is decisive, and close analyzed binaries afterward.
3. Add only the regression checks needed to protect that proof. Regenerate from player-only inputs, run strict recovery and pinned typed IL, compare declarations against the oracle, then compile, rebuild and run in the exact Windows editor/player.
4. Re-audit both broad denominators after a committed change. Record disposition and first-reason transitions, but treat them as analysis changes until whole-scope compilation and behavior pass. Keep alternate/original build settings and auxiliary inputs explicit.
5. Consolidate duplicated proven ABI, layout and emission logic as coverage grows. Profile before optimizing. Keep generated C# readable and Unity 2021.3 compatible, including block namespaces.

## Local validation commands

Run from the repository root. Configure the path variables locally from the ignored environment map; never put private paths or license data in this file or a commit. The Windows editor under Wine must use the existing licensed prefix.

```sh
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

For a fresh exact-target fixture round trip, set `UNITY_WINDOWS_EDITOR`, `WINE_BIN`, `MSVC_TOOLCHAIN_ROOT` and `UNITY_REFERENCE_DIR` to the supplied installation paths:

```sh
WINEPREFIX="$HOME/.wine_unity" python3 Validation/run_roundtrip.py \
  --profile composed-array \
  --editor "$UNITY_WINDOWS_EDITOR" \
  --wine "$WINE_BIN" \
  --toolchain-root "$MSVC_TOOLCHAIN_ROOT" \
  --cpp2il Cpp2IL/bin/Release/net10.0/Cpp2IL.dll \
  --reference-dir "$UNITY_REFERENCE_DIR" \
  --run-dir Files/runs/composed-array-check --timeout 900
```

The fixture and round-trip runners also accept `--code-generation OptimizeSize`
for an explicit Release variant; the default is `OptimizeSpeed`. Baseline reuse
requires the same recorded choice.

Use a fresh run directory. `--baseline-run` is valid only while its verified original project and player remain available; rebuild a pruned original. Review the receipt, BuildReport, typed-IL result and behavior counts before claiming acceptance. Use the same exact target for new fixtures; macOS checks must be labeled separately.

After reviewing a completed run, preview cleanup and then apply the same scope. This removes generated project/player trees while retaining small receipts, logs and recovered source. Preserve active investigations and any player inputs needed for later tests.

```sh
python3 Validation/prune_generated_artifacts.py --runs \
  --only composed-array-check --min-age-hours 0 --retain-player-input --show-paths
python3 Validation/prune_generated_artifacts.py --runs \
  --only composed-array-check --min-age-hours 0 --retain-player-input --apply
```

Make reviewed, public-safe local checkpoint commits only. Stage explicit files, inspect the staged patch for private data, and do not push.
