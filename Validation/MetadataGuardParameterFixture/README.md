# Static field plus parameter after a metadata guard

This neutral single-method fixture isolates a 32-bit static-field read and
parameter addition after a runtime TypeInfo initialization guard. Its original
Windows x64 Release IL2CPP body must be checked before a native proof is
accepted. The original editor and player behavior, including signed overflow,
are independent validation oracles.
