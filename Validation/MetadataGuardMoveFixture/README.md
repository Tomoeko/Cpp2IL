# Metadata guard with a scheduled register move

This neutral probe tests whether Unity 2021.3.35f1 Windows x64 Release IL2CPP
schedules an ordinary register move between a byte-sized runtime metadata flag
comparison and its branch. The flag must be independently tied to the installed
metadata initializer and a TypeInfo slot. It is not a managed Boolean field.

`AddParameter` isolates integer data flow. `AddBox` keeps an object argument live
across the initializer and also checks null behavior. Neither source type has a
class constructor. A complete native guard and original editor/player behavior
are prerequisites for any recovery claim. If the compiler produces another
shape, this fixture is only a negative compiler observation.
