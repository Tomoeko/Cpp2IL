# Integer division fixture

Eight methods separate signed and unsigned quotient/remainder operations at
32-bit and 64-bit widths. Every method defines a zero result for a zero divisor.
The signed methods also define the minimum-value divided by minus-one case as
a wrapped minimum quotient and zero remainder. These guards are authored
behavior, so this fixture does not prove general division exception recovery.

The separate validation driver observes boundary values, negative operands,
zero divisors and unsigned values above the signed range. Its Python oracle
uses integer arithmetic with truncation toward zero; it does not pass through
floating point or Python's floor-division semantics for signed operands.

Native recovery must distinguish DIV from IDIV, retain the instruction width
and prove the high half of the implicit dividend. An arbitrary high half or an
unproved partial-register operation must not be silently discarded. Keep the
original source and managed assemblies outside player-only recovery inputs.
The operand and exception contracts are documented in Intel's
[instruction-set reference, DIV and IDIV](https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-2a-manual.pdf).

Original compilation, recovered IL, source compilation, native rebuild and
behavioral comparison are separate gates. Fixture existence alone establishes
none of them.
