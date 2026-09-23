using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using AsmResolver.DotNet;
using Cpp2IL.Core.SourceEmission;

if (args.Length < 3)
    throw new ArgumentException("Arguments: original-managed-oracle.dll fresh-output-directory explicit-reference-directory [additional-reference-directory...]");
var assembly = AssemblyDefinition.FromFile(args[0]);
if (assembly.Name != "ComponentFixture")
    throw new ArgumentException("This emitter-only probe requires the synthetic ComponentFixture oracle assembly.");
var report = UnitySourceProjectEmitter.Emit([assembly], ["ComponentFixture"], args.Skip(2), args[1]);
var tool = typeof(UnitySourceProjectEmitter).Assembly.Location;
File.WriteAllText(Path.Combine(args[1], "component-emission-provenance.json"), JsonSerializer.Serialize(new
{
    inputKind = "original managed oracle; not player-only recovery",
    inputAssembly = Path.GetFullPath(args[0]),
    inputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))),
    emitterAssembly = tool,
    emitterSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tool))),
    explicitReferenceDirectories = args.Skip(2).Select(Path.GetFullPath).ToArray(),
    sourceGeneration = report.SourceGeneration
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Oracle-derived managed assembly emission only; this is not player-only recovery: " + report.SourceGeneration);
