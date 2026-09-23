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

Run the bounded harness checks without Unity or Wine:

```sh
python3 Validation/test_fixture_harness.py
```

They protect source/oracle separation, player-input filtering, incomplete
behavior-report refusal and the distinction between a deadline and a successful
process exit. Actual Editor/player validation remains a separate integration run.
