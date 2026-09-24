# Static 32-bit getter control

The two authored methods read mutable signed and unsigned 32-bit fields from
an ordinary class with no field initializer or class constructor. A separate
harness reads the initial defaults, writes boundary values, repeats reads,
and checks that both fields and an unrelated neighboring field are unchanged.

Build the original fixture with the supplied Unity 2021.3.35f1 Windows editor
as a Windows x64 Release IL2CPP player. Original source and managed output are
validation oracles. A successful original build does not establish recovered
source compilation or behavioral fidelity.
