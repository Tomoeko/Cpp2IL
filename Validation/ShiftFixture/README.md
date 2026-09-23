# Integer right-shift fixture

Four methods preserve arithmetic and logical right shifts for signed and unsigned 32-bit and 64-bit values. The independent harness covers five sign-boundary values and thirteen negative, zero, boundary and masked counts per width: 130 rows and 260 result checks. Python computes the expected signed interpretation and count masks independently.

Run `Validation/run_fixture.py --profile shifts` with the supplied exact Unity editor and an ignored run directory. `Validation/run_roundtrip.py --profile shifts` additionally performs player-only strict recovery, managed IL verification and a clean recovered-source native build. Compilation and these finite observations do not establish whole-program equivalence.

Validated with the supplied Unity 2021.3.35f1 Windows editor, .NET 4.x API profile and Release IL2CPP toolchain. Player-only recovery emitted all four selected methods without detected degradation; ILVerify 10.0.7 accepted the generated assembly against explicit Unity references. Recovered and rebuilt managed declarations matched the stripped original in the declaration comparer's projection, with zero differences. Both original and regenerated players passed all 260 finite result checks. These results cover this fixture, not all shifts or whole-program equivalence.

The x64 lifter distinguishes SAR from SHR and preserves 32/64-bit widths. Emission uses an Int32 count masked by 31 or 63 and rejects unknown/narrow widths or unproven result extensions. General partial-register moves, extension instructions, and division remain separate recovery work.
