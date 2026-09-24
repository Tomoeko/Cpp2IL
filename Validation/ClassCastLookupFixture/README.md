# Class-cast lookup control

The single explicit method reads an ordinary base-class reference field and
returns it as a derived class when the runtime type is compatible. The source
uses `as`; the recovery target is the evidenced reference-preserving result
and null behavior, not a claim that native code uniquely identifies C# syntax.

The behavior oracle covers null and incompatible field values, exact and
subclass matches, reference identity, aliases, a null owner, and unchanged
neighbor fields. This small fixture isolates the class-cast lookup from the
separate literal, TypeInfo-only, and combined-caller controls in the broader
runtime-cast fixture.
