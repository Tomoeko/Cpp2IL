# Terminal managed throw control

This synthetic fixture isolates two parameterless corlib exception throws in
instance methods. They differ only in the exception type. The fixture also
contains a returning helper call followed by field writes and a normal return.
That method is a negative control for treating a terminal decoded `CALL` as
nonreturning without proving its target.

The independent behavior oracle checks the exact exception types, null-owner
calls, signed integer wraparound, post-call field effects, and unchanged
neighbor state. Both throw methods match a complete 17-instruction allocation,
constructor, and terminal raise proof in the supplied Unity 2021.3.35f1
Windows x64 Release IL2CPP player. Strict player-only recovery emits all five
selected methods; pinned ILVerify and both declaration comparisons pass.
Generated source compiles in the supplied Windows editor and rebuilds as a
Win64 Release IL2CPP player. Original and recovered editor/player runs each
pass all nine observations. The accepted local receipt is under
`Files/validation/throw-only-roundtrip-01/roundtrip.json`. Arbitrary terminal
calls and other throw shapes remain unsupported. A subsequent player-only
recheck after tightening the MethodDef metadata route kept five of five
strictly recovered methods and byte-identical generated C# and Unity project
configuration. The local parity receipt is under
`Files/validation/throw-only-methoddef-parity-01/parity.json`. It did not
repeat the Unity build or behavior checks. The
fixture does not verify stack-trace text or other exception internals.
