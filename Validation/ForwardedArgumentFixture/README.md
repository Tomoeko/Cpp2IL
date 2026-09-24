# One-argument call through a reference field

The selected assembly has two implicit constructors and two authored methods.
`ForwardOwner.Forward` calls a nonvirtual method through one ordinary reference
field, forwarding its first argument and leaving its second argument unused.
The target increments a signed counter and returns a Boolean predicate. The
separate harness checks the forwarded argument, ignored argument, call count,
signed overflow, aliasing, neighboring fields, null receiver and null owner.

The supplied Windows Unity 2021.3.35f1 editor built the original source as a
Windows x64 Release IL2CPP player. `Forward` has a complete, file-backed,
handler-free eight-instruction body: load the field receiver into RCX, branch
to the runtime null helper when absent, clear the hidden method-metadata
argument, and tail-call one uniquely bound nonvirtual target. The existing
generic null-guard coalescer already recovers this ordinary Int32 argument
case. Strict player-only recovery emits all four selected methods, pinned
typed IL verification passes, and both declaration comparisons have zero
differences. Regenerated source compiles in the exact Windows editor and
rebuilds as a Windows x64 Release IL2CPP player. Original and recovered editor
and player runs each pass twelve independent observations. This is a
validation control for an existing path, with no new recovery proof or broad
coverage gain. Keep source and original managed assemblies separate from
player-only recovery inputs.
