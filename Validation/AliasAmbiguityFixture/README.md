# Shared native body ambiguity fixture

`First` and `Second` have the same owner, signature, and body. `CallFirst`
names one managed member. The exact Windows x64 Release IL2CPP build determines
whether the linker gives the two leaf members one native address and whether
the call carries a distinguishing `MethodInfo*`.

If both native addresses are shared and the hidden argument is null, the player
input cannot establish which managed member the call named. Strict recovery
must leave `CallFirst` unresolved. The original managed source is a validation
oracle only.
