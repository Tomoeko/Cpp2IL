# Guarded sink class-initialization control

This synthetic control has two ordinary, nonvirtual instance methods with
`void(Exception)` signatures in the selected assembly. Each forwards its
argument and a distinct string literal to a static method in the embedded
`Neutral.GuardedSink` package. The sink has an explicit static constructor
and records initialization and call order in a separate package witness.

The independent behavior oracle checks a null owner before the sink is warm,
then cold and repeated calls through two owners and a receiver alias. It
checks the literal, the exact exception reference including null, call
counts, static initialization order, and untouched receiver fields. The
package source and compiled assembly are explicit auxiliary validation
oracles, separate from player-only recovery inputs.

The original-source control built with Unity 2021.3.35f1 for Windows x64
Release IL2CPP. Editor and Windows player behavior each matched all nine
independent observations. Both forwarding methods have an exact 93-byte,
20-instruction handler-free native body with the once flag, two metadata
initializations, sink class-initialization guard, and a direct tail call. The
second method's inferred raw span is longer than its 93-byte unwind body, so
native proof must use the unwind boundary.

The initial strict player-only recovery of the selected assembly emitted its
constructor and failed both forwarding methods: **1 Emitted, 2 Failed**. That
run used the player binary and metadata without the package assembly or
original source. It is a recovery baseline, separate from the successful
original-source behavior control.

The bounded proof now emits all three selected methods from player inputs.
Pinned IL verification and two original-oracle declaration comparisons pass
with zero differences. Fresh generated source compiles in the exact Windows
editor and rebuilds as Windows x64 Release IL2CPP with zero errors. Original
and recovered editor and player behavior each match nine observations. The
final proof adds an indexed-memory guard; regenerated source and configuration
remain byte-identical to the accepted Unity run, and pinned IL verification
passes again. The package assembly is an explicit auxiliary input for source
reference closure, separate from player-only body recovery. Other guarded
static calls and class-initialization forms remain unverified.
