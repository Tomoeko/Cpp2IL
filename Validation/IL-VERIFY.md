# Managed IL verification

`verify_managed_il.py` runs Microsoft's pinned `dotnet-ilverify` **10.0.7** against explicitly selected generated assemblies. ILVerify checks ECMA-335 metadata and IL types; this is stronger than the emitter's branch-label and stack-depth checks. A passing result does not establish behavior, C# compilation, or a Unity player build. Unsafe but executable IL can also be unverifiable; such results are reported as failures, never suppressed.

Python 3.9+ and a compatible .NET 10 runtime are required. The optional installer writes the tool only to gitignored `Files/tools/ilverify`, with package/cache files under `Files/`. It uses the official NuGet source and does not install a global tool.

```sh
python3 Validation/verify_managed_il.py \
  --install-tool \
  --assembly Files/runs/recovered/Assembly-CSharp.dll \
  --reference-dir Files/target-references \
  --system-module mscorlib \
  --output-dir Files/runs/managed-il-check
```

Select every generated assembly with another `--assembly`, and every target reference directory with another `--reference-dir`. Reference directories are read nonrecursively. Unity's matching reference set must supply `mscorlib.dll` and all other required dependencies. Generated inputs can reference each other. No host framework, original application assembly, or validation oracle is added implicitly. A duplicate filename from distinct locations is rejected so an original application DLL cannot silently replace generated code.

The output directory must be new and gitignored under `Files/`. Each run records exact commands, verifier version, input/reference hashes, exit status, diagnostics and method/type counts in `result.json` and adjacent logs. Input/reference hashes must remain unchanged throughout verification. These artifacts may contain private names and paths; keep them untracked. No include/exclude filters or ignored verification errors are configured.

Runner exit statuses:

| Exit | Meaning |
| --- | --- |
| `0` | All selected assemblies completed verification without reported errors |
| `1` | ILVerify reported invalid or unverifiable IL/metadata |
| `2` | Unverified: missing tools/references, timeout, invalid configuration, or incomplete verifier evidence |

ILVerify's counters describe checked methods, including bodyless declarations; they are not recovered or behaviorally verified method counts. The runner also requires each assembly's completion marker and matching found/checked counts. Missing tools and reference-resolution errors cannot produce a pass.

Primary references: Microsoft's [ILVerify documentation](https://github.com/dotnet/runtime/blob/v10.0.7/src/coreclr/tools/ILVerify/README.md), [10.0.7 command-line options](https://github.com/dotnet/runtime/blob/v10.0.7/src/coreclr/tools/ILVerify/ILVerifyRootCommand.cs), [resolution and exit-status implementation](https://github.com/dotnet/runtime/blob/v10.0.7/src/coreclr/tools/ILVerify/Program.cs), and [official tool package](https://www.nuget.org/packages/dotnet-ilverify/10.0.7).
