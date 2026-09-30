using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AsmResolver.DotNet;
using ICSharpCode.Decompiler.Metadata;

namespace Cpp2IL.Core.SourceEmission;

/// <summary>
/// Keeps original Unity target references that source simplification can make unused.
/// An ordinary reference also exposes their types to generated source.
/// Portable PDB assembly aliases cause the supplied Roslyn compiler to retain the
/// AssemblyRef without introducing a type, attribute or executable dependency anchor.
/// Unity's response-file parser removes aliases from ordinary -reference arguments,
/// so Roslyn must read them from a nested, project-relative response file instead.
/// </summary>
internal static class UnityCompilerReferenceAliases
{
    private const string ResponseFileName = "ReferenceAliases.rsp";

    internal static List<string> Write(ModuleDefinition module, PEFile file, ExplicitAssemblyResolver resolver,
        string relativeDirectory, string sourceDirectory, List<string> diagnostics)
    {
        var aliases = new List<string>();
        var arguments = new StringBuilder();
        foreach (var handle in file.Metadata.AssemblyReferences.OrderBy(handle =>
                     file.Metadata.GetString(file.Metadata.GetAssemblyReference(handle).Name), StringComparer.Ordinal))
        {
            var reference = new ICSharpCode.Decompiler.Metadata.AssemblyReference(file, handle);
            var originals = module.AssemblyReferences.Where(original => original.Name?.ToString() == reference.Name).ToArray();
            // Preserve only authenticated target identities. Package and application
            // dependencies keep their existing explicit compilation configuration.
            if (originals.Length == 0 || !originals.All(original =>
                    Unity2021TargetAssemblies.HasTargetIdentity(original) ||
                    Unity2021TargetFrameworkAssemblies.HasTargetIdentity(original)))
                continue;
            if ((int)file.Metadata.GetAssemblyReference(handle).Flags != 0)
            {
                diagnostics.Add($"SOURCE014: {module.Assembly?.Name}: {reference.Name}: Compiler aliases cannot establish preservation of nondefault AssemblyRef flags.");
                continue;
            }

            // The compiler intrinsically uses the target core library. An extra
            // alias is unnecessary and can select it before Unity activates the
            // project's configured API profile on the first import.
            if (reference.Name == "mscorlib")
                continue;

            var resolved = resolver.Resolve(reference) as PEFile ??
                throw new InvalidOperationException("A target compiler reference must resolve to an explicit managed file.");
            var path = Path.GetFullPath(resolved.FileName).Replace('\\', '/');
            if (path.Contains('"') || path.Any(char.IsControl))
                throw new ArgumentException("A configured target reference path cannot be quoted safely for the Unity compiler.");
            // The supplied compiler accepts one alias per reference argument.
            // Its duplicate-file merge retains both global type visibility and
            // the portable-PDB alias that keeps an otherwise unused AssemblyRef.
            arguments.Append("-reference:\"").Append(path).Append("\"\n");
            arguments.Append("-reference:Cpp2ILReference").Append(aliases.Count).Append("=\"").Append(path).Append("\"\n");
            aliases.Add(reference.Name);
        }

        var compilerOptions = "-langversion:9.0\n-unsafe\n-checked-\n";
        if (aliases.Count > 0)
        {
            // Without portable symbols, unused aliases do not force an AssemblyRef row.
            // Resolved paths are explicit local target configuration, never copied DLLs.
            File.WriteAllText(Path.Combine(sourceDirectory, ResponseFileName), arguments.ToString(), new UTF8Encoding(false));
            compilerOptions += "-debug:portable\n@\"" + relativeDirectory + "/" + ResponseFileName + "\"\n";
        }
        File.WriteAllText(Path.Combine(sourceDirectory, "csc.rsp"), compilerOptions, new UTF8Encoding(false));
        return aliases;
    }
}
