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

The exact-profile recovery now accepts this closed native body, including both
typed array accesses and the intervening field effects in native order. It
requires the complete file-backed, handler-free method, the proved null and
bounds helpers, matching metadata and field layout, and no alternate native
entry. The selected strict player-only recovery emitted both methods without
fallback bodies; typed IL verification passed. Recovered declarations matched
the original stripped assembly with zero differences, and the original
stripped and unstripped declaration projections had no differences. The
recovered source compiled in the supplied Windows Unity editor, built a
Windows x64 Release IL2CPP player with zero errors and four Unity warnings,
and matched all 67 independent observations in both the editor and player.
The rebuilt declarations also matched the original with zero differences.

This proof is deliberately bounded to the complete native instruction shape.
It does not establish arbitrary sequences of array accesses or recover the
authored order of ordinary field stores when optimization has reordered them.
