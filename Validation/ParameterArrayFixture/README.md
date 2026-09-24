# Parameter-origin reference-array control

`ArrayReader.CompareWithMark` reads from two `object[]` parameters at one
shared `int` index. A no-inline method increments `Marker` between the reads.
The independent harness checks reference equality, null and bounds failures
at both access positions, aliased and distinct arrays, unchanged contents,
and signed marker wraparound.
The oracle observes whether the marker effect occurred; it does not prove its
order relative to unobservable reads. Native and final-graph analysis must
establish the two distinct array accesses and their effect order.

This fixture tests two parameter-origin array values. It makes no claim about
arrays returned from calls, reference-element stores, concurrent observation,
exception messages, stack traces, or original local names.
