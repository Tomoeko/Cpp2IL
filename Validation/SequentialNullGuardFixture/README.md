# Sequential reference null guards

This synthetic exact-target fixture has a signed `Int32` three-way comparison
over two reference boxes. The C# evaluation order reads the left field before
the right field. Null-left, null-right, both-null, equal and unequal values,
signed limits, aliasing and unchanged neighboring fields are observed by an
independent harness. A smaller one-reference method performs unchecked integer
work before a field read.

Build the original source with Unity 2021.3.35f1 Windows x64 Release IL2CPP,
then inspect its native guard order, exception exits and field accesses before
proposing a recovery rule. The source shape alone does not establish which
native pattern the optimizer will emit. A strict recovered-source round trip
must separately verify declarations, typed IL, Unity compilation, native
rebuilding and editor/player behavior.
