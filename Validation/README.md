# Exact-version synthetic validation

This fixture establishes a small Unity **2021.3.35f1 Windows x64 Release IL2CPP**
baseline for C# recovery. Its three methods exercise unchecked Int32 arithmetic,
signed branching, instance field mutation and a direct managed call. A separate
driver records 81 input pairs, including overflow boundaries. Python compares the
observations with independently calculated expected values. These finite vectors
are regression evidence, not proof of whole-program equivalence.

All source here is synthetic and redistributable under the repository license.
`Fixture/` is the application assembly to recover. `Harness/` is validation code,
outside that recovery scope. The harness must be supplied independently when
compiling replacement source; successful recovery of its own driver is not claimed.

Use the supplied exact editor; paths below are environment variables populated
from ignored local configuration. Each invocation requires a new run directory
under the repository's ignored `Files/`. It never edits the supplied source tree,
existing project, editor installation, or Wine license prefix.

```sh
python3 Validation/run_fixture.py --editor "$UNITY_EDITOR" \
  --run-dir Files/validation/source-compile --stage compile

python3 Validation/run_fixture.py --editor "$UNITY_WINDOWS_EDITOR" --wine "$WINE" \
  --toolchain-root "$WINDOWS_TOOLCHAIN" \
  --run-dir Files/validation/source-player --stage run --timeout 1200
```

`compile` performs exact-editor C# compilation and editor behavior checks.
`build` additionally requires a completed Windows x64 IL2CPP Release build.
`run` also executes the isolated player and checks its behavioral report.
Windows runs on non-Windows hosts require Wine and always use the existing
`$HOME/.wine_unity` prefix. A working Windows compiler/SDK is a separate local
prerequisite; the harness does not install one or modify the prefix.
`--toolchain-root` optionally supplies a VS2019 layout containing `VC/Tools/MSVC`
and `Windows Kits/10`. It sets compiler/SDK discovery hints inside the owned
Editor process and restores their prior values after building. Omit it when the
native environment already discovers the intended toolchain.

Each run creates a fresh project, local logs and `receipt.json`, including actual
commands and separate stage results. The project deliberately uses no downloaded
packages. Child processes use private temporary/.NET CLI directories, a fixed
locale and disabled .NET runtime roll-forward; these settings are recorded in
the receipt and do not change the parent environment or Wine prefix. Dedicated
scratch avoids sharing temporary files and .NET CLI cache state between runs.

Build settings select IL2CPP, native `Release`, nondevelopment mode,
the .NET 4.x API profile, low managed stripping and code generation optimized for
runtime speed. Unity 2021.3.35f1 reports that API profile as `NET_Unity_4_8`;
the Editor API's `NET_4_6` selector used by the harness maps to that value.
A zero process exit code is
insufficient: fresh completion artifacts and reported settings must also match.
Missing licensed tools or a failed build remain failed/unverified stages.

After a successful build, `player-input/` holds a copy of the shipped player files
without source, symbols or IL2CPP backup directories. Point Cpp2IL only at this
directory for player-only recovery. Original source, `project/Library`, generated
C++, and other oracle material remain outside the recovery input directory.
The filesystem split prevents accidental inclusion; it is not an OS sandbox.

To validate recovered source, use `--source-dir` with a directory containing the
`RecoveryFixture` assembly definition and recovered public API. The assembly must
expose `RecoveryFixture.RecoveryLogic`, its public Int32 `Value` field, static
`Add(int,int)` and `Select(int,int)`, and instance `Accumulate(int)`. This is an
explicit selected-slice contract. Do not repair generated method bodies by hand
or treat passing this slice as whole-player recovery. Each replacement run gets
its own fresh scratch project and reports `replacement-source` provenance.

Raw receipts contain local paths and input hashes and must stay in `Files/`.
Publish only sanitized stage summaries. Editor-only results, Windows build
results and native behavioral results must remain distinct in progress reports.

For the complete player-only round trip, build Cpp2IL first, then run:

```sh
python3 Validation/run_roundtrip.py --editor "$UNITY_WINDOWS_EDITOR" --wine "$WINE" \
  --toolchain-root "$WINDOWS_TOOLCHAIN" \
  --cpp2il Cpp2IL/bin/Release/net10.0/Cpp2IL.dll \
  --reference-dir "$UNITY_API_REFERENCES" --install-ilverify \
  --run-dir Files/validation/roundtrip
```

This creates the original fixture player, isolates only its binary and metadata
for strict source recovery, verifies recovered IL with the pinned verifier, then
compiles, rebuilds and runs the generated C# with the independent behavior driver.
All selected fixture methods, including the arithmetic fixture's constructor,
must be emitted without detected degradation. Original and rebuilt player
settings must match. Tool files are snapshotted and hashed so another local build
cannot change the run midway.

After recovery has finished, the independent declaration comparer consults the
original player's retained managed backup and the original unstripped compiler
output. It compares original stripped declarations with both recovered managed
IL and the rebuilt player's managed backup. Its projection includes attribute
constructor identity, and stripping losses are recorded separately. The runner
requires zero declaration differences or decode diagnostics; missing oracle
files fail the gate. Neither oracle is passed to Cpp2IL. The comparer is also
snapshotted and hashed. `roundtrip.json` records these declaration, IL, compilation,
native build and behavior gates separately; asset bindings remain unverified.

To reuse an existing successful original fixture build, add
`--baseline-run Files/validation/source-player`. The runner verifies the prior
build profile, native behavior result and player-input hashes before use. Every
recovery and replacement project still uses a fresh directory. Reference paths
must contain matching target API assemblies, never original application DLLs.

Completed runs can retain hundreds of megabytes of reproducible Unity build
output each. After reviewing a run's receipt and any artifacts needed for the
next validation step, preview and prune old generated trees:

```sh
python3 Validation/prune_generated_artifacts.py
python3 Validation/prune_generated_artifacts.py --apply
python3 Validation/prune_generated_artifacts.py --retain-player-input
python3 Validation/prune_generated_artifacts.py --retain-player-input --apply
python3 Validation/prune_generated_artifacts.py --runs --only RUN_NAME --retain-player-input --show-paths
python3 Validation/prune_generated_artifacts.py --runs --only RUN_NAME --retain-player-input --apply
```

The default dry run waits 24 hours after a terminal receipt. The script removes
only `project/`, `player/` and `player-input/` trees under completed ignored
`Files/validation/` runs. `--runs` selects ignored `Files/runs/` baseline runs
and requires at least one `--only RUN_NAME` so it cannot plan that entire tree.
The same receipt, age, symlink and ignored-path checks apply to both roots.
The script retains receipts, logs, recovered source and recovery reports,
copies small `project/Reports/` witnesses beside each run, and writes a private
cleanup manifest. Use `--retain-player-input` to keep
exact native fixture inputs while pruning their original project/player trees,
or `--exclude RUN_NAME` to keep an entire run under investigation. A run whose
player input was removed cannot be passed to `--baseline-run`; rebuild that
original fixture when a new player input is needed. Never use the cleanup
script on the supplied editor, license prefix, original game/project inputs,
or active runs.

Run the bounded harness checks without Unity or Wine:

```sh
python3 -m unittest discover -s Validation -p 'test_*.py' -q
python3 Validation/test_declaration_comparer.py
```

They protect source/oracle separation, player-input filtering, incomplete
behavior-report refusal and the distinction between a deadline and a successful
process exit. Actual Editor/player validation remains a separate integration run.
These public checks also run in the fork's ordinary CI; they require no private
inputs or licensed editor. The declaration comparer is a read-only managed
metadata tool, with separate declaration, stripping and body-validation scopes.
