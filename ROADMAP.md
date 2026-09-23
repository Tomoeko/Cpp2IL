# Unity 2021.3.35f1 recovery roadmap

## Goal and scope

Produce accurate, readable C# from **Unity 2021.3.35f1 Windows x64 Release IL2CPP** player inputs. Generated source must compile in the supplied exact editor, and recovered behavior must be verified against controlled source/build pairs. The target is **1:1 managed-structure and behavioral fidelity**. Unresolved or unavailable information remains a gap toward that target. Reproduction of erased source text or byte-identical native binaries is outside this accuracy definition.

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

Status: complete for the initial baseline. The complete solution restores and builds with .NET SDK 10.0.107; all 76 pre-change core tests passed. The latest full core run passes all 1,196 tests with the optional exact synthetic fixtures supplied and no skips. Native loop-call, scalar-truncation, array-read and exception-region integration checks are included. The current net10.0 CLI builds without warnings or errors. Sixty-nine public harness checks and the declaration comparer's mutation checks pass without Unity. The offline parser suite passes three local cases and explicitly skips five optional external samples. Four existing solution-wide package warnings concern the prerelease Disarm dependency. Private baseline logs are retained locally.

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

The following separate fixture scopes have complete native roundtrip receipts. Each passed player-only recovery, typed IL verification, declaration comparisons against the original managed oracle, exact-editor source compilation, a Windows x64 Release IL2CPP rebuild, and the listed original/recovered behavioral checks. Oracles are consulted after recovery; generated source is never repaired by hand.

| Fixture scope | Methods | Behavioral observations |
| --- | ---: | --- |
| Integer arithmetic and field mutation | 4 | 81 boundary/overflow operand pairs; constructor exercised |
| UInt32/UInt64 comparisons | 8 | 200 predicates across 50 operand pairs |
| Scalar struct parameters | 4 | 100 equality/addition results across 50 pairs |
| Signed/logical right shifts | 4 | 260 results across 130 rows |
| Guarded division/remainder | 8 | 596 results across 298 rows; explicit zero/minimum guards |
| Metadata literal | 1 | Repeated string value and object identity |
| Fresh component instances | 3 | 24 typed field observations and five helper outputs |
| Scalar floating predicates | 12 | 2,028 Boolean results across 338 pairs |
| Byte-field zero comparisons | 4 | 519 observations: all byte patterns, Boolean stores, unchanged fields and null receivers |
| Integer extensions | 12 | 1,364 results across 280 rows: all byte patterns and word/dword boundaries |
| Word-field zero comparisons | 4 | 786,438 checks: all short/ushort/char patterns, preserved fields, fresh defaults and managed null calls |
| Scalar binary64 truncation | 2 | 132 signed Int32/Int64 bit-pattern results across 66 inputs, including both limit boundaries, infinities and quiet NaNs |

These twelve fixtures have native acceptance receipts for 66 methods. At the scalar-truncation/exception-region checkpoint, all 64 previously accepted methods pass strict recovery and typed IL verification with unchanged method bodies and all 68 source/configuration files. The two added conversion methods pass a fresh complete native round trip. Finite fixture results establish neither general application equivalence nor support for all runtime initialization, exception or partial-register behavior. Receipts authenticate the tool snapshot, player inputs, build settings and generated artifacts locally. The integrated runner performs independent declaration comparisons before and after rebuilding; the earlier integer and scalar-struct scopes have equivalent separate comparison evidence.

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

A separate enum/interface/struct assembly passes strict player-only source recovery, typed IL verification, declaration comparison and exact macOS editor compilation. Its sole interface method correctly remains bodyless; zero methods are counted as recovered behavior. Explicit known input assemblies can therefore preserve declarations without invented implementations, while missing required native bodies and other exclusions still fail strict validation.

A separate eight-type attribute/parameter fixture exposed incorrect named-member types and boxed Type/array serialization. The writer fixes preserve all twenty emitted attributes without decode diagnostics, including null versus empty arrays and overloaded constructor identities. The complete declaration comparison still fails with eleven fact differences: two absent return rows/attributes, a readonly return signature lost in the native type record, and serialized Type-name qualification. These measured gaps are retained explicitly; passing the attribute serialization regression is not a complete declaration-fidelity result.

A separate attribute-array fixture verifies retained enum-array identity when boxed as an object. All 25 attributes decode, and its unstripped declaration comparison has zero differences. The stripped-oracle comparison retains one difference group for serialized Type-name qualification; the raw comparison does not normalize that remaining gap away.

- Verify registration, metadata tables, type/member ownership and address mappings against exact-target fixtures. Validate bounds and fail diagnostically on inconsistent inputs.
- Preserve namespaces, assembly identities, nested/generic types, constraints, inheritance, interfaces, overrides, explicit implementations, overloads and accessibility.
- Recover constructors, static constructors, properties/indexers, events, delegates, enum backing types, parameter modifiers/defaults, constants and supported attributes accurately.
- Verify field layout, value types, reference fields, generic substitution and static/thread-static storage. Do not confuse native object headers or runtime offsets with source-level layout directives.
- Emit valid identifiers and stable collision mappings. Preserve externally observable names, reflection behavior and serialized field identity; report cases that cannot be represented faithfully in C#.
- Use Unity-compatible syntax and the correct API/reference assemblies, package dependencies and assembly definitions. Respect Unity's [C# compiler restrictions](https://docs.unity3d.com/2021.3/Documentation/Manual/CSharpCompiler.html); the host tool's language version is unrelated to the output contract.

Exit evidence: declaration comparisons pass for the declared fixture set; generated application assemblies compile in the exact editor without duplicate engine/framework types or hidden reference substitutions. Incomplete bodies remain separately reported.

## Milestone 3 — Windows x64 Release semantics

Status: in progress through the established end-to-end harness. The arithmetic, unsigned-predicate, scalar-struct and right-shift scopes pass the complete native round trip. Parameter identity, by-reference writes and constructor-fusion boundaries have executable synthetic IL regressions. Dead-code elimination preserves potentially throwing evaluations; propagation and copy coalescing respect effects, aliasing, type identity and native widths. Native operations and opaque calls invalidate overwritten status flags; only proved flag values can survive into managed output, and unused unknown flag results can be removed.

The scalar-struct bridge preserves the managed parameter and loads its single proved primitive field. It requires a Windows x64 by-value parameter, default sequential blittable layout, matching 32/64-bit size and legal field access. Padded, multi-field and reference-containing neighboring fixtures all reject strict recovery. Right-shift emission distinguishes sign fill from zero fill, retains native operand width, masks the count to five or six bits and rejects unknown widths or unproved destination extensions. A bounded native byte/word-memory comparison against zero now requires an unchanged exact-width field layout and a single captured read. Volatile barrier/test shapes, packed or overlapping fields, 32-bit address truncation and indexed/RIP-relative operands remain rejected. General narrow comparisons, partial-register semantics and the broader groups below remain active work.

Division recovery now requires a native basic-block proof that the high half of the implicit dividend is zero or the correct sign extension. It distinguishes DIV/IDIV, snapshots operands before overwriting either result, preserves 32/64-bit width and rejects unproved wider dividends. Executable IL tests cover unsigned high bits, zero divisors and signed quotient overflow even when only the remainder is used; native end-to-end acceptance is the guarded eight-method scope above.

Broad metadata/class-initialization deletion and literal-argument call substitution have been retired. A positive exact-profile proof accepts only a closed metadata-literal guard with a known helper and matching later literal load; Boolean-field branches and class initializers remain explicit. Recovery-region cleanup also retains potentially throwing arithmetic, memory/array evaluations and escaped values. A computed-address store alone no longer establishes a GC bitmap write; that optimization requires further provenance before it can resume.

Unknown native calls also remain explicit: transitive exception-name strings and call-chain hints no longer substitute guessed allocation/throw behavior. A fresh verification of all seven accepted scopes still recovers 32 of 32 methods with valid typed IL and 44 byte-identical generated source/configuration files. This reuses the authenticated native-run evidence because those generated inputs are unchanged.

Explicit null/bounds exception guards now remain until a matching managed access proves that their condition and exception ordering are preserved. Four executable regressions prevent exception-name-only cleanup from changing a throw into an ordinary return. Scalar floating predicates have a separate width and four-outcome representation, with 12,544 executed managed comparisons covering all sixteen outcome masks; the twelve-method native round trip also passes. Its vectors cover finite values, infinities, both zeros, subnormals and quiet NaNs. Memory operands, signaling-NaN exception status and externally altered floating control state remain separate scopes.

Integer extension now records source width, result width and sign/zero fill explicitly. A closed native parameter-to-return proof preserves EAX zeroing separately from RAX sign extension; unproved conversion forms no longer become ordinary moves. Its twelve-method fixture passes strict recovery, typed IL verification, both declaration comparisons, exact-editor compilation and the recovered native round trip with 1,364 results across 280 rows. Retiring ordinary MOVZX moves initially exposed an unproved shift-count setup. A bounded proof now retains the actual Int32 mask and emits an explicit low-byte zero extension only when every use is a supported register shift count. At the validated word/shift checkpoint, all 64 selected methods pass strict recovery and typed IL: ten scopes (60 methods) have identical source/configuration and method IL, and the four changed shift methods pass a fresh complete native roundtrip with 260 results. Comparisons distinguish method bodies from whole-DLL hashes, which may differ with module identifiers.

Native body boundaries also fail explicitly when no terminal path is proved, rather than inventing a return. Real returns and closed control flow leave that diagnostic unreachable. Rewritten throws repair block successors and positional phi inputs before unreachable code is removed. Missing receiver-local warnings clear only when independent native reads/writes prove the incoming receiver unused; ambiguous mappings keep their diagnostics. Stack analysis now checks exit paths independently, preserves warnings on unbalanced returns, allows explicit throws to unwind, rejects inconsistent stack positions at real joins and checks offset overflow. Eleven focused regressions pass; this stack change is later than the completed native checkpoint.

A separate scalar-truncation baseline preserves two binary64-to-signed-integer methods. The exact Windows editor and original Release player each pass 132 bit-pattern results across 66 inputs, including representable neighbors of both integer limits, infinities and quiet NaNs. Native inspection confirms the two register conversion/return bodies. A bounded native proof now emits an explicit binary64-to-signed conversion with guarded managed IL, preserving signed minimum output for NaN and overflow without relying on unspecified managed cast results. Ninety-one focused checks pass, including actual native bodies, independent bit-pattern calculations and rejection controls. Both methods now pass strict player-only recovery, typed IL verification, both declaration comparisons with zero differences, exact Windows editor compilation and behavior, and a rebuilt Release IL2CPP player with all 132 results. This scope verifies result bits with masked floating exceptions; floating status flags and externally changed control state remain separate.

Exact-profile x64 recovery now checks the PE exception directory and every reachable native instruction before lifting. Malformed, chained or handler-bearing unwind regions remain unsupported; frame-free leaves require independent register and control-flow checks. Sixty-two focused checks pass, including two original native catch methods that the production lifter explicitly rejects. This protects reporting while catch/finally recovery remains unfinished. A separate null-throw helper investigation found constructor-exception suppression in the runtime, so ordinary allocation/construction/throw lowering remains disabled.

The exact target runtime null-check helper now has a native instruction, metadata-identity, read-only literal and unwind proof. Its operation remains explicit until an SSA guard proves that the null arm is exclusive and that the other arm reaches the same receiver's nonvirtual call without moving side effects across the check. The emitter then retains the implicit receiver check through managed `callvirt`; an uncoalesced helper remains a strict failure. The four-method loop/direct-call fixture passes strict player-only recovery, typed IL, zero-difference declaration comparisons before and after rebuilding, exact Windows editor compilation and a Release IL2CPP player rebuild. Original and recovered editor and player behavior each cover 983 observations, including null receivers and zero-count bypasses. The 187 focused checks also pass. This is a bounded native-call and null-guard proof, not catch/finally or general exception-construction support.

A synthetic `int[]` read control now passes strict player-only recovery, typed IL verification, zero-difference declaration comparisons before and after rebuilding, exact Windows editor compilation and a fresh Release IL2CPP player rebuild. The original and recovered editor and player each pass 28 observations, including null arrays, negative indices and upper-bound failures. A closed native proof checks the successful element load, both distinct nonreturning runtime exception helpers, and the caller's unwind region before emitting an implicit managed array access. This adds one bounded method to the accepted scope; it does not establish array writes, other element types, exception-message or stack-trace identity, or general bounds-helper lowering.

A two-method exception-region control now passes the exact Windows editor and original Release IL2CPP player with 14 observations each. It exercises an explicit managed throw caught by type and a finally side effect on normal and exceptional exits. Both player-only recoveries currently fail strict validation at the native-handler gate, as required; neither is counted among accepted methods. Its x64 unwind entries carry both handler flags, and [Microsoft's x64 exception format](https://learn.microsoft.com/en-us/cpp/build/exception-handling-x64?view=msvc-170) leaves the following handler data language-specific. A preliminary divide-by-zero fault variant passed editor behavior but crashed in the Wine player; the passing control uses an explicit managed throw to isolate EH recovery from host fault handling. That failed native run remains local and supplies no behavioral acceptance evidence.

The unwind reader now preserves validated handler flags, executable handler identity, and the address of file-backed read-only handler data. Synthetic malformed-pointer and mutable-data controls reject invalid evidence. Both exact-target methods expose distinct handler data through this structural reader and still fail strict recovery; the language-specific try, catch, and unwind maps have not yet been established as managed exception regions. This checkpoint adds no recovered method or Unity rebuild claim.

A later SIMD audit removes eleven unproved scalar aliases: raw-bit and full-vector moves, packed bitwise operations, and incomplete shuffle/interleave expansion. Unsupported cases remain explicit even when their results are overwritten; 56 focused decoding, dead-code and emission checks pass. At this checkpoint all 66 accepted methods across twelve scopes still pass strict recovery and typed IL verification. All 74 source/configuration files and all 66 method-body projections are unchanged against their native acceptance receipts; whole DLL byte identity is not claimed.

The frame-free x64 proof now accepts a near `RET imm16` only when its immediate is zero. In that case it pops the return address with no additional stack adjustment, as specified by the [Intel instruction reference](https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-2b-manual.pdf). A read-only native inspection and 31 focused checks distinguish that encoding from returns that do change the caller's stack. The broader corpus refresh below measures the resulting disposition changes without treating them as behavior validation.

After that return-proof change, a fresh receipt-driven refresh of the current committed CLI accepts 70 of 70 selected methods across thirteen scopes with strict recovery and typed IL. All 80 source/configuration files and all 70 method-body projections are unchanged against their completed native receipts. This refresh did not launch Unity; the loop-call scope has the fresh native round trip above. Whole managed DLL byte identity is not claimed.

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

Status: complete native round trips pass for arithmetic, unsigned comparisons, scalar structs, right shifts, guarded division, metadata literals, fresh component instances, scalar floating comparisons, ordinary byte/word-field zero comparisons, integer extensions, scalar binary64 truncation, bounded direct-call loops with null receivers, and a bounded `int[]` read. The runner requires independent declaration comparisons before and after rebuilding; the loop-call and array-read runs have zero differences at both gates. Original managed oracles are consulted only after player-only recovery. Broader assembly/project configuration and script discovery/bindings remain active work. Original script GUIDs and asset bindings are not reconstructed by the current code-only exporter.

A separate emitter-only component probe now passes in the supplied Windows editor with `NET_Unity_4_8`: two component scripts map to their expected classes and fresh-instance script assets, all six serialized field paths/types/kinds match, and the ordinary helper and assembly attribute survive. The previous single-file output compiled but discovered zero of the two scripts. Deterministic class-matching files, safe namespace/assembly directories and explicit name/collision diagnostics fix that measured defect. The source report mappings also match the editor's observed paths. This probe deliberately uses an authored managed oracle; it does not establish player-only body recovery or restoration of original GUIDs/scenes.

The later three-body component round trip recovers from isolated player inputs, passes typed IL verification and both declaration comparisons with zero differences, and rebuilds/runs in the exact Windows editor. Two fresh instance pairs retain six field paths before and after assignment, including private strings, a nested value field and object-reference identity. Source/declaration resolution uses complete assembly identities across explicit framework versions; ILVerify uses a separately recorded reference set without duplicate simple names. The same player-only generated source also passes a fresh exact-Windows-editor discovery probe: both component scripts bind to their expected classes and fresh instances, and all six serialized field paths/types/kinds match. The probe authenticates all five copied source/configuration files against the passed recovery receipt both before and after the editor run. These runtime observations do not reconstruct serialized scenes or original script GUIDs.

- Expand the minimal exporter to a deterministic application-source/project layout, assembly references, packages, platform defines and required helpers. Identify dependencies that player metadata cannot reconstruct and require explicit local configuration for them.
- Compile in a fresh project with the supplied Unity 2021.3.35f1 editor. Use its documented [batch mode and editor entry-point arguments](https://docs.unity3d.com/2021.3/Documentation/Manual/EditorCommandLineArguments.html); validate completion and generated outputs as well as process status. Do not suppress compiler errors or use stale assemblies to pass.
- Build the Windows x64 player with IL2CPP and native `Release` settings explicitly selected. Record Development Build, stripping, code-generation and toolchain settings; run the built fixture in the supported local environment.
- Compare original and rebuilt behavior with deterministic inputs. Record platform/timing-sensitive cases and mismatches rather than claiming equivalence from a successful launch.
- Verify MonoBehaviour/ScriptableObject identity, serialized fields and script bindings where supporting asset data is available. Treat missing asset/project information as a separate limitation, not permission to invent original GUIDs or scene data.

Exit evidence: a clean regeneration/import/build/run cycle with reproducible settings, no compiler errors, and no unreported fallback or excluded method in the declared verified scope. macOS import checks and Windows player results remain separately labeled.

## Milestone 5 — Private integration and maintainable coverage

Status: baselines measured; integration acceptance remains incomplete. Every run retains all 7,256 selected private methods and all 12,724 selected independent-application methods. The table records analysis dispositions only; both complete scopes fail strict recovery in every run. Neither corpus has an accepted source project, typed-IL result or behavioral equivalence result.

| Recovery checkpoint | Corpus | Emitted | Failed | Partial | No managed body |
| --- | --- | ---: | ---: | ---: | ---: |
| Initial baseline | Private | 1,832 | 4,393 | 14 | 1,017 |
| Initial baseline | Independent | 4,927 | 7,214 | 104 | 479 |
| Width, flag and ABI checks | Private | 1,874 | 4,357 | 8 | 1,017 |
| Width, flag and ABI checks | Independent | 4,847 | 7,299 | 99 | 479 |
| Initialization/exception preservation; floating and byte-field support | Private | 1,672 | 4,561 | 6 | 1,017 |
| Initialization/exception preservation; floating and byte-field support | Independent | 4,415 | 7,655 | 175 | 479 |
| Integer-extension and native-boundary proofs | Private | 1,664 | 4,572 | 3 | 1,017 |
| Integer-extension and native-boundary proofs | Independent | 4,210 | 7,955 | 80 | 479 |
| Null-check and SIMD safety proofs | Private | 1,696 | 4,542 | 1 | 1,017 |
| Null-check and SIMD safety proofs | Independent | 3,717 | 8,448 | 80 | 479 |
| Zero-adjustment near-return proof | Private | 1,734 | 4,504 | 1 | 1,017 |
| Zero-adjustment near-return proof | Independent | 4,293 | 7,872 | 80 | 479 |
| Bounded `int[]` read proof | Private | 1,734 | 4,504 | 1 | 1,017 |
| Bounded `int[]` read proof | Independent | 4,293 | 7,872 | 80 | 479 |

The integer-extension checkpoint authenticated prior inputs and preserved every selected and full-input method identity. Ten private and 300 independent methods previously marked emitted failed on unproved integer extensions. Two private and 95 independent partial methods became emitted solely because a native proof resolved their unused-receiver mapping warning. One additional private partial method failed on an unproved extension. The fallthrough boundary check became the first diagnostic for 490 private and 1,264 independent methods that already failed.

The current corpus refresh again authenticates the input and complete method denominators. Relative to the preceding null-check/SIMD checkpoint, the zero-adjustment return proof moves 38 private and 576 independent methods from failed to emitted, with no emitted-to-failed transition. Both full application scopes still fail strict recovery; neither has a typed-IL, Unity compilation, native rebuild or behavioral equivalence result. First-diagnostic counts are not root-cause counts, and disposition changes do not establish behavioral accuracy.

A subsequent authenticated player-only refresh after the bounded array-read proof preserves all 7,256 private and 12,724 independent selected identities and dispositions, with no transition in either direction. The new proof therefore adds only its controlled synthetic acceptance result at this checkpoint. Both broad scopes still fail strict recovery and have no typed-IL, Unity compilation, native rebuild or behavioral equivalence result.

- Run the user-supplied source/build pair locally as an independent validation case. Keep source/original assemblies inaccessible to the player-only recovery step, and use them afterwards for comparisons.
- Validate the private target with the same reporting, compilation and behavioral gates to the extent an oracle is available. Without source or an equivalent oracle, report the narrower observed evidence honestly.
- Convert general failures into minimal synthetic regressions. Never encode private game names, method names, offsets, hashes or input-specific exceptions into tracked recovery logic.
- Publish only sanitized aggregate findings: explicit corpus scope, feature coverage, compile/build results, behavioral counts, exclusions and remaining defects. Set measurable coverage targets from the baseline before making a 1:1 accuracy claim.
- Deduplicate proven common parsing/ABI/type/emission logic, simplify stale paths, and profile costly analysis. Preserve diagnostics and regression evidence during optimization.
- Make public synthetic tests practical for this fork's CI without private files, licenses or publishing credentials. Keep licensed/editor-dependent runs optional and explicitly labeled; CI setup does not authorize pushing or publishing.

Exit evidence: increasing coverage on controlled and independent inputs, explainable remaining gaps, repeatable results and no private information in tracked changes. A compiling project alone does not satisfy this milestone's accuracy goal.

## Checkpoints and completion claims

After each coherent implementation change, run checks appropriate to its risk, review the staged diff for private information, make a local checkpoint commit, and update the relevant milestone with a short sanitized evidence summary. Keep raw logs and long investigation trails out of this roadmap. Only the user pushes.

Support claims must state the exact tested profile and corpus, declaration fidelity, source compilation and native-build results, behavioral coverage, and unresolved counts. Seventy-one methods across fourteen separate fixtures have verified recovered Unity projects and native behavioral receipts: the earlier receipt-driven refresh covered seventy methods in thirteen scopes, and the new array-read scope adds one fresh exact-target round trip. There is currently no measured 1:1 accuracy result for a broader corpus or the private target.
