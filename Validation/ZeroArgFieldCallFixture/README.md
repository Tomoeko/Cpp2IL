# Zero-argument call through a reference field

The selected assembly has two implicit constructors and two authored methods.
`CallOwner.Forward` loads one unchanged reference field and invokes the
nonvirtual, zero-argument `CallReceiver.Touch` method. `Touch` increments one
signed 32-bit field without inlining. Separate fields on both classes check
that the call and its receiver load leave neighboring state unchanged.

The independent harness checks repeated calls, signed overflow, two owners
sharing one receiver, a null receiver, and a null owner. Build the original
fixture in the supplied Unity 2021.3.35f1 Windows editor as a Windows x64
Release IL2CPP player. Its source and original managed assembly are validation
oracles; passing the original build does not establish recovered-source
compilation or behavior. Inspect the exact native method shape before adding
a recovery rule, and keep unsupported call shapes explicit.
