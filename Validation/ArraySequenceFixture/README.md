# Sequenced array field access fixture

The selected assembly contains an ordinary class with an `int[]` field, an
observable counter, a record of the first value read, and one authored method.
The method reads an element, records that value, increments the counter, then
writes the value to a second element of the same field. Its implicit constructor
also belongs in the assembly-wide strict recovery denominator.

The independent `ArraySequenceHarness` and `array_sequence.py` oracle cover
null owners and arrays, empty and populated arrays, negative and excessive
indices at both access sites, same-index and different-index writes, signed
values, counter wraparound, and another owner that either shares or does not
share the array. If the first access fails, neither the counter nor the array
changes. If the second access fails, the first value and counter change while
the array does not. One second-access failure starts the counter at
`int.MaxValue` and checks that its earlier increment wraps to `int.MinValue`.
Exception types and state are checked; messages and stack traces are not.

The runner profile is `array-sequence`, with assembly `ArraySequenceFixture`
and two selected methods. A fresh original build in the supplied Windows
Unity 2021.3.35f1 editor passed source compilation and a Windows x64 Release
IL2CPP player build with zero errors and four Unity warnings. Original editor
and player behavior each matched all 67 independent observations.

The original native body has a complete handler-free unwind region. It loads
the array field, branches to a proved null-throw helper, performs an unsigned
bounds check and sign-extends the first index before a 32-bit element read. It
then increments the counter and stores the observed value, checks the second
index against the same retained array, sign-extends that index, and performs a
32-bit element write. Both bounds failures reach the same proved nonreturning
helper. The compiler placed the counter increment before the observed-value
store, opposite to the authored C# order. Player-only recovery therefore
cannot establish that original source order from this native body; concurrent
observation of those ordinary fields is outside this fixture's behavior check.

The current strict player-only selected diagnostic emits the constructor but
rejects `ReadThenWrite`: the bounds helper is proved nonreturning, while the
caller's managed bounds and exception semantics remain unresolved. No
recovered-code Unity compilation, native rebuild, or behavior claim follows
from the original build.
