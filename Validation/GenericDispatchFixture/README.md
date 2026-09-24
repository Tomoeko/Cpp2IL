# Generic interface dispatch control

This synthetic fixture isolates two private, final, new-slot implementations
of a generic interface. Each zero-argument implementation passes a signed
`Int32` literal to a protected generic method on an ancestor in the embedded
`Neutral.GenericDispatch` package. The constructed method type argument is
the interface method's return type. The package also contains a non-generic
overload with the same name and parameter type. Both overloads have identical
effects so an exact Release build may fold their native bodies to one address.

The package and selected assembly deliberately have no explicit static
constructors. The exact player metadata confirms four selected method
identities: one abstract interface method, one class constructor, and two
explicit interface implementations. The harness is a separate assembly and is
excluded from selected recovery.

The independent oracle checks the two closed return types and explicit
interface mappings through reflection. It checks the signed keys, per-instance
call counts, interface/base receiver aliases, unchanged neighboring references,
the retained non-generic overload, and null interface calls through behavior.
Identical folded overload bodies cannot establish the call's generic identity
from behavior alone; the MethodRef metadata and native target binding must
establish that identity in a later recovery proof.

The original Unity 2021.3.35f1 Windows x64 Release IL2CPP build passed exact
editor compilation, native player build, and independent editor/player behavior
checks (9/9 observations in each). Both wrappers have complete, handler-free
62-byte/14-instruction bodies. Each has a distinct initialized MethodRef slot
and once flag; its MethodRef names the constructed generic ancestor method,
and its native tail matches that method's definition address.

That address has **three** recorded managed aliases in this player: the generic
method definition, the same-name non-generic overload, and a concrete
`System.Object` generic instantiation. The two-alias target seen in the broad
cohort has therefore not been reproduced here. This fixture is an original
native-shape and behavior control; it does not support relaxing an alias-count
gate or claim recovery of those broad methods.

A preproof strict, player-only source attempt exited with status 1 and
unresolved behavior: the selected constructor was Emitted, the abstract method
was NoManagedBody, and both wrappers Failed on the unproved metadata
initialization guard and its dependent native zero-flag value (1 Emitted,
2 Failed, 1 NoManagedBody of 4).

The bounded MethodRef proof now emits the constructor and both wrappers from
player inputs; the abstract interface method remains NoManagedBody. Pinned IL
verification and both declaration comparisons against the original stripped
managed oracle pass with zero differences. Fresh generated source compiles in
the exact Windows editor and rebuilds as Windows x64 Release IL2CPP with zero
errors. Original and recovered editor/player runs each pass nine independent
observations. The final proof rejects target classes with static constructors
and MethodRef slots affected by PE base relocations; its source and
configuration are byte-identical to the Unity-accepted run, and pinned IL
verification passes again. The package source, reference map and compiled
assembly are explicit synthetic auxiliary inputs for source reference closure;
they were not body-recovery inputs. Other generic call and alias shapes remain
unverified.
