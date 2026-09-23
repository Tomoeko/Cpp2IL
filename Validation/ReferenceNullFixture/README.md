# Managed reference/null comparisons

This exact-target fixture exercises native 64-bit equality and inequality
against zero for unchanged managed class and single-dimensional array
references. It covers unchanged direct class and array parameters, null and
non-null inputs, empty and populated arrays, and an untouched neighboring
field. The `reference-null` round-trip profile compiles and rebuilds the
recovered source.
