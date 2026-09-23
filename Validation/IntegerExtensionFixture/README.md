# Integer extension fixture

Twelve separately preserved methods expose sign and zero extension from 8, 16
and 32 bits to 32 or 64 bits. The driver tests every byte pattern and selected
word/dword boundaries, recording hexadecimal output bits so signedness and JSON
number precision cannot hide a difference. An independent Python oracle derives
the expected bits arithmetically.

Use the `integer-extensions` fixture profile with the exact supplied editor.
Native EAX writes clear RAX's upper 32 bits, while AX/AL writes preserve their
neighbors; the native lifting proof must keep this distinction. A sign extension
to EAX followed by an unsigned extension to RAX differs from a direct sign
extension to RAX. See the instruction definitions in
[Intel's software developer manuals](https://www.intel.com/content/dam/develop/external/us/en/documents-tps/253665-sdm-vol-11.pdf).

The fixture is not a recovery acceptance claim until strict player-only recovery,
typed IL verification, both declaration comparisons, exact-editor compilation,
native rebuilding and independent behavioral checks pass. Memory loads, high-byte
registers, merged partial-register writes and arbitrary conversion sequences need
their own evidence.
