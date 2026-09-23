# Exact 16-bit field equality fixture

`WordFieldFixture` isolates three zero-equality predicates over `short`, `ushort`,
and `char` instance fields. Its expected selected scope is four managed methods:
the implicit constructor and the three predicates. Verify the actual unstripped
and shipped-metadata identities after the authored exact-target build.

The independent `WordFieldHarness` enumerates all 65,536 input bit patterns for
each predicate. It stores each Boolean truth table as an 8,192-byte bitset encoded
with base64; bit `n & 7` of byte `n >> 3` records input pattern `n`. The signed
field interprets the same pattern as an unchecked `short`, and `char` is tested as
a 16-bit code unit, including unpaired surrogate values without string conversion.

Every predicate invocation also records whether each of the three fields remained
unchanged. Non-target fields contain distinct nonzero sentinels, so reading the
wrong field cannot silently match the target's zero case. The oracle separately
checks 196,608 predicate outcomes and 589,824 field-preservation outcomes, plus
three fresh field values and three ordinary managed null-receiver calls.

Null calls check normal managed call behavior. They do not establish how directly
calling a native method pointer with a null receiver behaves. The driver and its
`link.xml` are disclosed validation configuration, outside the selected recovery
assembly. Original source is an oracle; recovery must use only the isolated
player binary and metadata.

The authored baseline passed source compilation, editor observations, a Windows
x64 Release IL2CPP build, and all 786,438 native-player observations in the supplied
Unity 2021.3.35f1 editor. The unstripped and shipped declarations retain one type,
four methods, and three fields with no lost identities. Player-only inspection
confirms all three predicates use a direct word-sized comparison with a
sign-extended eight-bit zero immediate followed by `SETE`.

No recovered-code compilation, typed-IL, or behavioral pass has been recorded for
this fixture yet. It is independent of the larger unresolved narrow-comparison
fixture. Support for packed or overlapping layout,
volatile/barrier sequences, indexed or address-size-overridden memory, partial
register arithmetic, and nonzero or ordered comparisons is outside this scope.
