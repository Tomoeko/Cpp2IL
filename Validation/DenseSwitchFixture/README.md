# Dense switch decode-boundary control

This neutral assembly has one 16-way dense `int` switch and one sparse-switch
control. Each branch mutates a by-reference trace and returns an input-dependent
result. The independent harness covers every dense case, both sides of its
range, signed integer extremes, each sparse case, and overflow behavior.

The runner profile is `dense-switch`, assembly `DenseSwitchFixture`, with two
selected methods and the separate `DenseSwitchHarness`. A source `switch` does
not guarantee that MSVC emits an indirect jump or embeds its table inside a
function. The sparse method is a behavioral control for this native layout.

A fresh original build in the supplied Windows Unity 2021.3.35f1 editor
compiled and produced a Windows x64 Release IL2CPP player with zero build
errors. Original editor and player behavior each passed all 525 oracle
observations. Native inspection found a 16-entry table of image-relative jump
destinations in the executable section immediately after the dense method's
code. The sparse method became a direct compare-and-branch chain. The table's
presence is evidence for this fixture's decode boundary, not a general rule
for C# switches.

A later strict player-only recovery selected and emitted both methods without
detected degradation. The dense dispatch was proved as a closed CFG with
file-backed, relocation-free table entries, an unsigned default guard, exact
case instruction starts, and no unaccounted reachable exits. Its 16 cases are
lowered to managed equality branches while ordinary lifting retains their
effects and returns. Width-authenticated native loads and arithmetic establish
the by-reference `Int32` trace type. The sparse compare chain uses the regular
lifter. This proof applies to the checked native forms only.

The exact-target round trip passed pinned managed IL verification and two
declaration comparisons against validation-only original assemblies, each
with zero differences or stripping losses. Recovered source compiled in the
supplied Windows Unity 2021.3.35f1 editor, built as Windows x64 Release
IL2CPP with zero errors, and matched all 525 independent editor and player
behavior observations. Original and recovered native builds each reported
four warnings. The emitted C# is readable but has expanded arithmetic and
locals; the finite behavior oracle does not establish original syntax.
Version-29 player metadata leaves return-row and return-attribute presence
unknown, so player-only declaration fidelity remains explicitly partial.
