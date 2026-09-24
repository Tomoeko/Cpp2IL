# Unsigned byte threshold control

`ByteThresholdState.HasHighBit` compares an ordinary instance `byte` field
against 128. The separate runtime harness checks values on both sides of the
threshold, unchanged field and neighboring state, and the null receiver. The
assembly has one authored method and an implicit constructor.

Build this fixture in the supplied Unity 2021.3.35f1 Windows editor for a
Windows x64 Release IL2CPP player. Recovery must use only the isolated player
binary and metadata; the authored source and original managed assembly are
validation oracles. A passing original player does not establish recovered
source compilation or behavioral fidelity. Unsupported narrow comparisons,
volatile fields, and overlapping layouts remain separate scopes.
