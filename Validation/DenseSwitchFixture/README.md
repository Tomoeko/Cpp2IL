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

A separate strict player-only diagnostic selected both methods and rejected
both: the dense method has an unproved native exit at its indirect jump, and
the sparse method has an unresolved local type. Neither method has accepted
recovered managed IL, Unity source compilation, a rebuilt player, or recovered
behavioral parity.
