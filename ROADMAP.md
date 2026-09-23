# Unity 2021.3.35f1 recovery roadmap

## Goal and scope

Produce accurate, readable C# from **Unity 2021.3.35f1 Windows x64 Release IL2CPP** player inputs. Generated source must compile in the supplied exact editor, and recovered behavior must be verified against controlled source/build pairs. Near 1:1 means preservation of recoverable managed structure and observable behavior; it does not promise reproduction of erased source text or byte-identical native binaries.

The user has confirmed this roadmap and `AGENTS.md` and authorized full implementation. Milestones 0 and 1 have passed their initial synthetic acceptance scope. Later milestones remain active; this is not a whole-project recovery certification.

Windows x64 is the initial platform. Other Unity versions, architectures, obfuscation variants, and full asset/scene reconstruction are outside the first delivery. Existing cross-platform support should remain usable where practical. Assets are not prerequisites for a code-only recovery milestone; serialization and scene compatibility require separate evidence when claimed.

## Inspected starting point

Baseline inspection: repository revision `b5ad444` on `development`.

| Area | Observed implementation | Consequence |
| --- | --- | --- |
| Parsing | `LibCpp2IL/Metadata/Il2CppMetadata.cs` includes Unity 2021.3 metadata handling | Parsing support does not establish exact-version code recovery |
| Analysis | `MethodAnalysisContext.Analyze()` lifts instructions, constructs control flow, and runs stack/SSA, metadata and recovery passes | Improve and validate this pipeline before replacing it wholesale |
| Managed output | `AsmResolverDllOutputFormatIlRecovery` emits IL, skips selected framework/engine bodies, and falls back to stubs or throwing bodies | Current success counts do not prove complete recovered behavior |
| IL validity | `Cpp2IL.Core/IlGenerator.cs` disables automatic max-stack computation and contains unsupported-instruction diagnostics and missing-value default substitutions | Validate stack/type correctness and report degradation explicitly |
| C# output | `DiffableCsOutputFormat` writes declaration snapshots with empty bodies and file-scoped namespaces | This is not a Unity-compatible implementation or project exporter |
| Tests | Existing parser/core tests cover other Unity fixture versions; no dedicated 2021.3.35f1 validation path was found in the inspected loaders | Add a small exact-target fixture and an end-to-end acceptance path |
| Build setup | Current projects/CI use .NET 10; `global.json` selects the test runner without pinning an SDK | Establish the actual local baseline; do not rely on stale README versions |
| CI | The existing test/publish job is restricted to the upstream repository | Fork build status alone must not be presented as test evidence |

The older local recovery tool supplies investigation ideas only. Its outputs and plans are not evidence that this fork can compile or reproduce behavior. The baseline observations above describe the inspected starting revision; implementation evidence is recorded below.

## Acceptance model

Keep these gates distinct in reports. Scope each result to an explicit fixture/input set and settings.

| Gate | Required evidence |
| --- | --- |
| Input/profile validity | Exact editor version, PE/x64 input, metadata/registration consistency, and recorded known/unknown build settings |
| Declaration fidelity | Assembly/type/member signatures, generic constraints, attributes, constants and layout compared with a known oracle |
| Managed IL validity | Valid instructions, references, control-flow joins, stack/type behavior and exception regions for methods represented as recovered IL |
| Unity compilation | Fresh generated C# imports and compiles in Unity 2021.3.35f1 with the intended platform symbols, references and API profile |
| Native rebuild | Fresh Windows x64 IL2CPP player build using C++ compiler configuration `Release`, with a successful build report and artifacts |
| Behavioral fidelity | Original and recovered builds agree on tested return values, mutations, side effects, dispatch, initialization and exceptions |
| Unity integration | When in scope, component identity, serialized fields and script/asset bindings are verified independently |

Unity's [IL2CPP build pipeline](https://docs.unity3d.com/2021.3/Documentation/Manual/IL2CPP.html) includes managed compilation, stripping, C++ conversion and native compilation. A managed compile is therefore only one acceptance stage. The [C++ compiler configuration](https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Il2CppCompilerConfiguration.html) must be `Release`; a .NET `-c Release` build, an unchecked Development Build option, or a `Master` native build is not equivalent evidence.

Report total input methods and types, methods with native bodies, emitted bodies, partial/unsupported bodies, fallbacks, failures, and exclusions with reasons. Record compilation results and behaviorally verified method counts separately. Generic/shared native addresses must not collapse distinct managed methods in the denominator. Never improve a metric by silently dropping hard cases or counting stubs as recovered.

## Milestone 0 — Reproducible baseline and truthful reporting

Status: complete for the initial baseline. The complete solution restores and builds with .NET SDK 10.0.107; all 76 pre-change core tests passed. The expanded core suite passes 307 tests, and the current net10.0 CLI builds without warnings or errors. Twelve public harness checks and the declaration comparer's mutation checks pass without Unity. The offline parser suite passes three local cases and explicitly skips five optional external samples. Four existing solution-wide package warnings concern the prerelease Disarm dependency. Private baseline logs are retained locally.

Implemented evidence:

- Recovery reports retain every input method, distinguish exclusions and failures, and reject detected incomplete recovery in strict mode. Labels and stack depth are checked separately from typed IL verification and behavior. Regression cases reject missing values and unsupported operations rather than inventing defaults.
- A synthetic assembly with three authored methods and one implicit constructor builds in the supplied exact Windows editor. Its original Windows x64 IL2CPP Release player passes 81 independent integer boundary/overflow input pairs. Actual settings are nondevelopment, `NET_Unity_4_8`, Low stripping, OptimizeSpeed, MSVC 14.29.30133 and Windows SDK 10.0.19041.0. This is original-fixture evidence, not recovered-source evidence.
- The fresh-project harness records editor compilation, editor behavior, native build and player behavior independently. It refuses empty behavioral reports, stale output and timed-out processes even if they later exit successfully. Private player loading also succeeds; metadata-only stubs remain explicitly unverified behavior.
- Fork CI now runs the offline suites, and publishing stays restricted to the original repository. No remote workflow or push was performed.
- The synthetic player baseline contains 11,904 input methods. Default DLL and diffable-source output produce 37 metadata/stub assemblies and 1,775 declaration files. These counts are not recovered behaviors. The initial recovery report counts seven emitted bodies, 23 unresolved and 11,874 explicit exclusions across the player; the verified selection is only the four-method synthetic fixture assembly.

- Confirm the local .NET SDK, package restore, repository build, and relevant existing tests. Distinguish infrastructure failures from code failures. Record a working SDK baseline and resolve stale build guidance when justified.
- Inventory the supplied editor, Windows IL2CPP support, Wine environment and native toolchain. Record versions and usable host/target combinations locally; do not infer the original player's compiler flags from a directory name.
- Define a target profile for 2021.3.35f1, PE x64 and Release. Record API compatibility level, stripping, code-generation options, symbols, compiler/SDK versions and other known settings. Label unknown player settings instead of silently assuming them.
- Add a small, synthetic, redistributable source fixture built with the exact profile. Preserve original source, managed output and generated native material as separate validation oracles; run recovery from an isolated copy of player inputs.
- Baseline current DLL and diffable-source output, then add explicit method dispositions and reasons. Cover empty analysis, method-size limits, unsupported instructions, default substitutions and caught failures.
- Keep public fixtures/configuration neutral and private raw runs under `Files/`. Separate ordinary public tests from optional local integration runs; unavailable licensed tooling must be reported as skipped/unverified.

Exit evidence: reproducible commands and settings; an honest feature/error inventory with fixed denominators; no existing recovery limitation disguised as successful behavior.

## Milestone 1 — One complete recovery path

Status: complete for the initial four-method slice. The `cs_unity` exporter uses recovered CIL and pinned ICSharpCode.Decompiler 9.1.0.7988, explicit target references and selected application assemblies. All four methods recovered from isolated native player inputs pass typed IL verification. Their freshly generated C# compiles in Windows Unity 2021.3.35f1, rebuilds as Windows x64 Release IL2CPP, and passes all 81 independent input pairs in both editor and native player. No generated source was manually repaired. The constructor is exercised as a required dependency. This establishes finite tested behavior, not whole-program equivalence.

A separate negative strict run selects the more complex validation-driver assembly: four of its six methods remain unresolved, and strict mode rejects source output while retaining the full disposition report. The original managed DLL was used separately to validate the decompiler component; it was not supplied to the successful player-only recovery command.

The same complete round-trip runner also passes for eight UInt32/UInt64 comparison methods: isolated player inputs, zero selected fallbacks, typed IL verification, exact-editor compilation, native Release rebuild, and 200 predicate observations across 50 boundary operand pairs. Original and recovered build settings match. Receipts hash the tool snapshot, input files and generated artifacts locally.

- Choose a tiny vertical slice with constants, arithmetic, a conditional, a field read/write and a direct managed call. Build it through the exact target, recover it, regenerate source, compile in Unity, rebuild and compare observable results.
- Audit the slice's metadata resolution, calling convention, lifting and IL generation. Remove guessed-value and unsupported-operation substitutions from its verified path. Check stack balance/types and control-flow joins explicitly.
- Make an evidence-based emitter choice: validated CIL plus a compatible C# decompiler, or structured typed IR to C#. Evaluate correctness, Unity syntax, dependency licensing and maintenance with this same fixture. Share managed models; avoid divergent declaration logic.
- Emit a minimal project with real target references and assembly boundaries. Keep diagnostic `diffable-cs` behavior distinct from a new compilable-source contract.
- Establish the headless validation harness now, with fresh output directories, bounded execution, logs, exit codes, completed compilation/build signals and machine-readable results.
- Regenerate and revalidate from clean player inputs. Include a deliberately unsupported case to prove the reporting/strict validation path does not claim success for it.

Exit evidence: one end-to-end, player-input-only source recovery with exact Unity compilation, Windows x64 Release IL2CPP rebuild and passing behavioral comparisons. No fallback body or manual source repair is accepted in the verified slice.

## Milestone 2 — Metadata and source declaration fidelity

Status: in progress. A separate exact-target declaration fixture preserves 10 types, 35 methods, 25 fields, seven properties, two events, six generic parameters and 27 attributes through stripping. A metadata comparer checks identities and declaration facts independently of method bodies; its own mutation checks detect changed declarations. Correcting declared packing removes one measured layout defect. Two raw differences remain: a zero declared size becomes the effective native size, and an original marshaling descriptor is unavailable. Exact-editor/native reflection probes retain these differences rather than normalizing them away.

A separate marshaling fixture proves that Boolean `I1` and `U1` descriptors yield identical field type, flags and marshaled-size evidence. Size alone cannot recover the original annotation. Source output explicitly reports missing marshaling descriptors and strict mode rejects that incomplete declaration. No guessed `MarshalAs` annotation is accepted.

An independent application comparison now passes with zero differences and zero decode diagnostics: 1,769 types, 12,724 methods, 10,798 fields, 2,664 properties, 42 events, 43 generic parameters, 8,848 attributes and 29 assembly references match the supplied managed backup. Version-29 empty-string parsing, reference preservation and the finalizer mapping resolve all 35 initially measured differences. This is the comparer's declaration projection against that backup, not recovered-body, source-compilation or behavior evidence; a separate unstripped oracle is unavailable.

Declared assembly dependencies now survive emission even when no retained signature uses them. The writer preserves allocated reference rows, with a regression that removes all consumer types before writing and reloading the assembly. A separate seven-type, 13-method exact-target finalizer fixture passes player-only declaration comparison with zero differences: both destructors retain their canonical `Object.Finalize` mappings, while inherited finalizers and unrelated virtual methods gain no invented mappings.

- Verify registration, metadata tables, type/member ownership and address mappings against exact-target fixtures. Validate bounds and fail diagnostically on inconsistent inputs.
- Preserve namespaces, assembly identities, nested/generic types, constraints, inheritance, interfaces, overrides, explicit implementations, overloads and accessibility.
- Recover constructors, static constructors, properties/indexers, events, delegates, enum backing types, parameter modifiers/defaults, constants and supported attributes accurately.
- Verify field layout, value types, reference fields, generic substitution and static/thread-static storage. Do not confuse native object headers or runtime offsets with source-level layout directives.
- Emit valid identifiers and stable collision mappings. Preserve externally observable names, reflection behavior and serialized field identity; report cases that cannot be represented faithfully in C#.
- Use Unity-compatible syntax and the correct API/reference assemblies, package dependencies and assembly definitions. Respect Unity's [C# compiler restrictions](https://docs.unity3d.com/2021.3/Documentation/Manual/CSharpCompiler.html); the host tool's language version is unrelated to the output contract.

Exit evidence: declaration comparisons pass for the declared fixture set; generated application assemblies compile in the exact editor without duplicate engine/framework types or hidden reference substitutions. Incomplete bodies remain separately reported.

## Milestone 3 — Windows x64 Release semantics

Status: in progress through the established end-to-end harness. The initial signed Int32 arithmetic/branch/field/call slice and eight UInt32/UInt64 predicates pass the complete native round trip. Parameter identity, by-reference writes and constructor-fusion boundaries have executable synthetic IL regressions. Dead-code elimination preserves potentially throwing evaluations; propagation and copy coalescing respect effects, aliasing, type identity and native widths. Native operations and opaque calls invalidate overwritten status flags; only proved flag values can survive into managed output, and unused unknown flag results can be removed. Small-struct ABI projection, shift widths and the broader feature groups below remain under active investigation.

Prioritize measured failure categories rather than adding broad pattern collections without evidence.

| Feature group | Required focus |
| --- | --- |
| ABI and calls | Argument registers/stack, shadow space, hidden method/context arguments, return buffers, struct passing/returns, thunks and indirect calls |
| Control/data flow | Flags, signed/unsigned comparisons, integer widths, joins/phi values, loops, switches, aliasing, tail calls and optimized/inlined code |
| Object/runtime behavior | Allocation, constructors, class initialization, null/bounds checks, boxing/unboxing, casts, field access and runtime helper effects |
| Arrays and values | Element layout, by-reference access, value-type copies, floating-point edge cases and vector instructions as encountered |
| Generics and dispatch | RGCTX, generic sharing, inflated signatures/layouts, virtual/interface calls, delegates and shared native bodies |
| Exceptions | Throw/rethrow, catch/finally/filter regions and cleanup; missing recovery must stay explicit |
| Generated constructs | Closures, iterators/coroutines and async state machines, preserving behavior even when original syntax is unavailable |

Use Binary Ninja when native ambiguity needs investigation. Generalize each supported pattern from controlled fixtures and validate its preconditions. Remove runtime helpers only when their managed effects are preserved; do not erase initialization or exception behavior to simplify output.

Exit evidence per feature: a targeted regression where needed, valid output, exact-editor compilation, and behavioral comparisons including meaningful boundary cases. Unsupported cases continue to fail strict validation with actionable reasons.

## Milestone 4 — Unity project and build verification

Status: the single-command round-trip runner passes for both arithmetic and unsigned-comparison fixtures, with separate typed IL, source compilation, native build and behavioral gates. Broader assembly/project configuration and script/asset bindings remain pending. Script/asset bindings are not reconstructed by the current code-only exporter.

- Expand the minimal exporter to a deterministic application-source/project layout, assembly references, packages, platform defines and required helpers. Identify dependencies that player metadata cannot reconstruct and require explicit local configuration for them.
- Compile in a fresh project with the supplied Unity 2021.3.35f1 editor. Use its documented [batch mode and editor entry-point arguments](https://docs.unity3d.com/2021.3/Documentation/Manual/EditorCommandLineArguments.html); validate completion and generated outputs as well as process status. Do not suppress compiler errors or use stale assemblies to pass.
- Build the Windows x64 player with IL2CPP and native `Release` settings explicitly selected. Record Development Build, stripping, code-generation and toolchain settings; run the built fixture in the supported local environment.
- Compare original and rebuilt behavior with deterministic inputs. Record platform/timing-sensitive cases and mismatches rather than claiming equivalence from a successful launch.
- Verify MonoBehaviour/ScriptableObject identity, serialized fields and script bindings where supporting asset data is available. Treat missing asset/project information as a separate limitation, not permission to invent original GUIDs or scene data.

Exit evidence: a clean regeneration/import/build/run cycle with reproducible settings, no compiler errors, and no unreported fallback or excluded method in the declared verified scope. macOS import checks and Windows player results remain separately labeled.

## Milestone 5 — Private integration and maintainable coverage

Status: baselines measured; integration acceptance remains incomplete. The independent application scope contains 12,724 methods: 4,927 emitted, 7,214 failed, 104 partial and 479 without managed bodies. The private target scope contains 7,256 methods: 1,832 emitted, 4,393 failed, 14 partial and 1,017 without managed bodies. These are initial analysis dispositions, not typed IL or recovered-source compilation successes; both strict runs reject output. Counts include every selected method. First-error categories guide investigation but are not root-cause counts. Subsequent fixes require fresh measured runs before claiming improved coverage.

- Run the user-supplied source/build pair locally as an independent validation case. Keep source/original assemblies inaccessible to the player-only recovery step, and use them afterwards for comparisons.
- Validate the private target with the same reporting, compilation and behavioral gates to the extent an oracle is available. Without source or an equivalent oracle, report the narrower observed evidence honestly.
- Convert general failures into minimal synthetic regressions. Never encode private game names, method names, offsets, hashes or input-specific exceptions into tracked recovery logic.
- Publish only sanitized aggregate findings: explicit corpus scope, feature coverage, compile/build results, behavioral counts, exclusions and remaining defects. Set measurable coverage targets from the baseline before making a near-1:1 claim.
- Deduplicate proven common parsing/ABI/type/emission logic, simplify stale paths, and profile costly analysis. Preserve diagnostics and regression evidence during optimization.
- Make public synthetic tests practical for this fork's CI without private files, licenses or publishing credentials. Keep licensed/editor-dependent runs optional and explicitly labeled; CI setup does not authorize pushing or publishing.

Exit evidence: increasing coverage on controlled and independent inputs, explainable remaining gaps, repeatable results and no private information in tracked changes. A compiling project alone does not satisfy this milestone's accuracy goal.

## Checkpoints and completion claims

After each coherent implementation change, run checks appropriate to its risk, review the staged diff for private information, make a local checkpoint commit, and update the relevant milestone with a short sanitized evidence summary. Keep raw logs and long investigation trails out of this roadmap. Only the user pushes.

Support claims must state the exact tested profile and corpus, declaration fidelity, source compilation and native-build results, behavioral coverage, and unresolved counts. The four-method arithmetic and eight-method unsigned-comparison fixtures have verified recovered Unity projects and native behavioral results. There is currently no measured near-1:1 accuracy result for a broader corpus or the private target.
