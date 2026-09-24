# Target-provided framework reference

This synthetic fixture places a `System.Numerics.BigInteger` field in an
application assembly. Its one authored method has a simple arithmetic body.
The separate harness checks the field's managed assembly identity and the
method's behavior in the exact Unity editor and Windows x64 IL2CPP Release player.

The fixture probes whether the source exporter classifies a framework assembly
provided by Unity's .NET 4.x API profile. It needs no extra package or plug-in.
The field identity is an auxiliary source/project test; native method recovery
must be validated separately from the original build and recovery reports.

`reference-map.json` is an explicit-input control for the existing map path.
Omit it when validating automatic recognition of the target-provided reference.
