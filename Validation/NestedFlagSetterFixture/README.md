# Nested Boolean field setter control

This synthetic control has an instance method with one unused
class-reference parameter. The method writes `true` through a directly declared
reference field to a Boolean field on a second ordinary class:
`Child.Flag = true`. The selected assembly contains the method and three
implicit constructors. Its harness is a separate assembly.

The independent oracle checks both null and non-null unused payloads, a false
and already-true flag, repeated calls, reference identity and neighboring
fields, a null child, and a null owner. The original Windows Unity 2021.3.35f1
editor and Release IL2CPP player each pass all 22 observations. An earlier
player-only strict recovery fails only the setter, with the runtime null guard
unresolved. The bounded proof emits all four selected methods from the player;
managed IL verification and both independent declaration comparisons pass with
zero differences. Fresh generated source compiles and rebuilds in the same
Windows Unity editor, and recovered editor/player runs each pass the 22
observations. These finite observations do not establish arbitrary nested
stores, volatile behavior, or exception-detail identity.
