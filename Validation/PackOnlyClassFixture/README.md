# Packed reference-class source control

This synthetic exact-target control asks whether a reference-class declaration
with nondefault `Pack` and omitted `Size` can produce version-29 player metadata
whose class-size-default flag remains set. Five field/layout shapes omit `Size`;
an explicit-zero and a larger-size class are controls. The managed and player
metadata must be inspected separately. Omitted source `Size` alone does not
prove that the player preserves the distinction.

Stage this assembly beside the ordinary arithmetic fixture in a fresh ignored
source directory, then build with the supplied Unity 2021.3.35f1 Windows editor
using `Validation/run_fixture.py --profile arithmetic --stage build`. The
arithmetic runner checks its own behavior; it does not establish behavior of
these packed classes. Keep the Unity project, player and metadata report under
ignored `Files/`, and inspect both stripped and unstripped managed assemblies
before pruning the generated project and player.

In the measured Windows x64 Release IL2CPP build, all seven reference classes
retained managed `Pack = 2`. The five omitted-size classes and the explicit-zero
control retained managed `Size = 0` both before and after stripping; the larger
control retained `Size = 32`. Every version-29 player type had nondefault
packing and a nondefault class-size flag. Varying empty, primitive, reference,
and explicit field layouts did not produce a player type at the pack-only
writer gate. A strict player-only source export emitted all seven constructors
but rejected the assembly with `DECL003` and `SOURCE011`: the authored `Size`
was unavailable and no reference-class `ClassLayout` row was emitted. This is
an original-build and declaration-evidence control, with no accepted recovered
Unity compilation, native rebuild, or class-behavior result.

After inspection, use `Validation/prune_generated_artifacts.py` with `--only`,
`--retain-player-input`, and `--show-paths` first as a dry run, then with
`--apply`. Preserve the stripped and unstripped managed oracles under the
ignored run directory before pruning.
