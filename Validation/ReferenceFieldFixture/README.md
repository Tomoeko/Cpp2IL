# Reference-field read control

Build this neutral fixture with Unity 2021.3.35f1 Windows x64 Release IL2CPP. The methods read a string field directly or through one reference field. The harness checks returned value and reference identity for nonempty, empty and null strings, plus null receivers at both levels. Array fields before and after each read target exercise unchanged layout when array type wrappers are rematerialized. Derived owners add fields after the inherited storage and exercise the same reads to test base-field identity and layout.
