# Boolean second-argument branch fixture

This neutral fixture checks the native control flow for a static method whose
second managed argument is Boolean. On Windows x64 that argument may arrive in
the low byte of the second argument register. The no-inline callees keep both
branch targets observable. The exact Unity 2021.3.35f1 Release IL2CPP player
must be inspected before treating a low-byte self-test as a supported native
shape; a different compiler shape is a negative control.

The original source and independent oracle verify both branch outcomes across
several otherwise unused first-argument values. They do not establish
recovered-code compilation or behavior until a strict player-only regeneration
also passes those gates.
