# Terminal managed throw control

This synthetic fixture isolates two parameterless corlib exception throws in
instance methods. They differ only in the exception type. The fixture also
contains a returning helper call followed by field writes and a normal return.
That method is a negative control for treating a terminal decoded `CALL` as
nonreturning without proving its target.

The independent behavior oracle checks the exact exception types, null-owner
calls, signed integer wraparound, post-call field effects, and unchanged
neighbor state. The two throw methods are candidates for a compact native
allocation, constructor, and terminal raise pattern in the supplied Unity
2021.3.35f1 Windows x64 Release IL2CPP build. Source shape alone does not
establish that native pattern or successful recovery; inspect the original
player before enabling a production proof.
