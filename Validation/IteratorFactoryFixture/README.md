# Iterator factory fixture

An instance iterator yields one object field. Its factory must create a fresh enumerator and capture the owner, while the generated iterator type supplies the enumeration methods. The player-only audit must inventory every retained method in this assembly before any recovery claim. A proof of the factory alone cannot establish strict whole-assembly recovery if the generated methods remain unresolved.

The independent harness checks fresh enumerators, deferred field reads, null values, enumeration completion, disposal and `Reset` behavior. The exact Windows Unity 2021.3.35f1 baseline passed its original editor and Release IL2CPP player checks. Strict player-only recovery emitted six of eight selected methods; the generated `MoveNext` and `Reset` bodies remain unresolved, so no recovered-source compilation or behavioral acceptance is claimed for this control.
