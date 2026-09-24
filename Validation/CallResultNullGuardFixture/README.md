# Call-result receiver null guards

This synthetic Unity 2021 C# 9 fixture makes `ChainRoot.Execute` call a
nonvirtual method that returns a nullable `ChainNode`, then call a nonvirtual
method on that result. The second call also returns a nullable node. `Trace`
changes before the first call and between each subsequent call, so the first
and second null results have distinct observable exception states.

`ExecuteRepeated` calls `GetRepeated` twice. Each returned node is a receiver
for a distinct nonvirtual call, with `ReadWith` used only for the second
result. The getter increments `GetterCalls` and selects `First` or `Second` by
the count. The `Marker` byte is stored between calls. The harness observes
call count, marker, trace, reference identity, both null exits, and signed
overflow. The first-null exit leaves the marker untouched, while the
second-null exit observes the write; moving the store across either call
changes behavior. Reusing the first result for the second call also changes
the result or exception state.

`ExecuteInheritedRepeated` is a separate variant. Its first getter result
receives a Boolean write through the public `Enabled` property inherited from
`InheritedFlagBase`. The property has a private backing field on the base.
The source writes `Enabled`, then reads the first node's `Value` before
storing `Marker` and calling the getter again. The value is captured before
the second call to expose the ordering of both guarded receivers. Direct
access to the inherited private backing field would be invalid C#; recovery
must emit a legal member access supported by metadata and native evidence.

The supplied Windows x64 Release IL2CPP build instead reads `first.Value`
before its inlined Boolean byte store, even though the authored source puts
the setter first. Its two getter calls and separately guarded results remain
observable. The metadata identifies the inherited public setter, and its
native body is an exact byte store followed by `ret`; the caller contains the
matching inlined byte store. This establishes a bounded behavior-preserving
setter representation for the tested single-threaded cases. The original
managed callsite identity and the authored IL ordering are unavailable from
this optimized player body. The fixture does not prove those two aspects of
1:1 source recovery.

The declaration contract is exactly one assembly, two public sealed classes,
one public unsealed base class, seven public mutable instance fields, one
private Boolean auto-property backing field, ten public nonvirtual methods
including two property accessors, and three public parameterless instance
constructors: thirteen methods in total. `ChainRoot` declares `First:
ChainNode`, `Second: ChainNode`, `Trace: int`, `GetterCalls: int`, `Marker:
byte`, `GetFirst(): ChainNode`, `Execute(): int`, `GetRepeated(): ChainNode`,
`ExecuteRepeated(): int`, and `ExecuteInheritedRepeated(): int`. `ChainNode`
derives from `InheritedFlagBase` and declares `Next: ChainNode`, `Value: int`,
`GetNext(): ChainNode`, `Read(): int`, and `ReadWith(int): int`.
`InheritedFlagBase` declares `Enabled: bool` with public get/set accessors.
The behavior harness checks this contract by reflection. Preserve the original
stripped managed assembly as the stronger declaration oracle for a
recovered-source round trip.

Validate against a fresh Unity 2021.3.35f1 Windows x64 Release IL2CPP build.
Strict recovery, valid managed IL, declaration comparison, Unity source
compilation, native player rebuilding, and editor/player behavior are separate
validation stages. The proof deliberately rejects unproved alias bindings,
altered native call or store locations, and stores whose receiver register
cannot be tied directly to the field access. A copied call-result receiver or
an SSA Phi merge remains unsupported until its producer can be bound to the
native callsite. An SSA copy of the receiver before the byte store is also
unsupported.
