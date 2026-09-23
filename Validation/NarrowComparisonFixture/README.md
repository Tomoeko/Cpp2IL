# Narrow comparison and initialization controls

This synthetic assembly separates native metadata bookkeeping from managed state and initialization effects. Keep the driver outside this assembly, and build with Unity 2021.3.35f1 Windows x64 Release IL2CPP. The retained methods are investigation controls; they are not a claim that all cases are recovered.

| Control | Required observation |
| --- | --- |
| `ObserveCondition` | For all four initial Boolean pairs, `Observed` becomes `Observed || Condition`; `Condition` is unchanged. |
| `IsByteZero` | True only for zero across byte values 0, 1, 127, 128 and 255. |
| `HasByteHighBit` | True for 128 and 255; false for 0, 1 and 127. |
| `IsSignedNegative` | True for -128 and -1; false for 0, 1 and 127. |
| `IsWordZero` | True only for zero across values 0, 1, 255, 256 and 65535. |
| Metadata literal and type token | Return the declared neutral string and the exact `ByteState` type on repeated calls. |
| `ReadInitialized` | Repeated calls return 17; `CompletedCount` increases exactly once in a fresh process. |
| `TouchThrowing` | Repeated calls throw `TypeInitializationException` with `InvalidOperationException` inside; `ThrowingCount` increases exactly once. |

A metadata guard may be removed only with positive proof of its runtime helper, guard location, effects and control flow. An application Boolean or byte branch is not such proof. Class initialization can run user code or throw; removing its call requires preserved managed initialization semantics. Narrow register comparisons and broader signed/unsigned cases remain separate work.
