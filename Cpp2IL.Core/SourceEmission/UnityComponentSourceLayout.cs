using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.TypeSystem;

namespace Cpp2IL.Core.SourceEmission;

internal static class UnityComponentSourceLayout
{
    internal sealed class SourceFile(string path, string text, string? componentType = null)
    {
        public string Path { get; } = path;
        public string Text { get; } = text;
        public string? ComponentType { get; } = componentType;
    }

    public static List<SourceFile> Split(SyntaxTree tree, DecompilerSettings settings, string assemblyName, List<string> diagnostics)
    {
        var candidates = new List<(TypeDeclaration Declaration, ITypeDefinition Type, string Path)>();
        foreach (var declaration in tree.Descendants.OfType<TypeDeclaration>())
        {
            if (declaration.GetSymbol() is not ITypeDefinition type || !IsUnityComponent(type))
                continue;
            if (type.DeclaringType != null || type.TypeParameterCount != 0)
            {
                diagnostics.Add($"SOURCE006: {assemblyName}: {type.FullName}: Nested or generic component script discovery is unsupported.");
                continue;
            }
            try
            {
                if (type.Name != declaration.Name)
                    throw new ArgumentException("The emitted class name differs from its managed identity.");
                ExplicitAssemblyResolver.ValidateSimpleName(type.Name);
                if (!string.IsNullOrEmpty(type.Namespace) && type.Namespace.Split('.').Any(string.IsNullOrEmpty))
                    throw new ArgumentException("A namespace contains an empty path segment.");
                if (type.Namespace.Split('.').Any(p => p.EndsWith("~", StringComparison.Ordinal)))
                    throw new ArgumentException("Unity ignores a namespace directory ending in '~'.");
                if (!IsRepresentableIdentifier(type.Name) ||
                    !string.IsNullOrEmpty(type.Namespace) && type.Namespace.Split('.').Any(p => !IsRepresentableIdentifier(p)))
                    throw new ArgumentException("The component class or namespace identifier cannot preserve its metadata name in C#.");
                var emittedNamespace = declaration.Ancestors.OfType<NamespaceDeclaration>().FirstOrDefault()?.FullName ?? "";
                if (type.Namespace != emittedNamespace)
                    throw new ArgumentException("The emitted namespace differs from its managed identity.");
                // Namespace text must not create Unity folders such as Editor/Resources/cvs.
                var namespaceParts = string.IsNullOrEmpty(type.Namespace) ? [] : type.Namespace.Split('.').Select(p => "ns-" + p).ToArray();
                foreach (var part in namespaceParts)
                    ExplicitAssemblyResolver.ValidateSimpleName(part);
                var path = "Components/" + string.Join("/", namespaceParts.Append(type.Name + ".cs"));
                candidates.Add((declaration, type, path));
            }
            catch (ArgumentException error)
            {
                diagnostics.Add($"SOURCE006: {assemblyName}: {type.FullName}: Component identity cannot be represented as a portable class-matching source path. {error.Message}");
            }
        }

        // Keep every colliding declaration in the central source file and report the gap.
        // Case and Unicode normalization checks also protect imports on other desktop hosts.
        var collisions = candidates.GroupBy(c => c.Path.Normalize(NormalizationForm.FormC), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).SelectMany(g => g).ToArray();
        foreach (var collision in collisions)
        {
            candidates.Remove(collision);
            diagnostics.Add($"SOURCE006: {assemblyName}: {collision.Type.FullName}: Component source paths collide on a case-insensitive filesystem.");
        }

        var files = new List<SourceFile>();
        foreach (var component in candidates.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            var componentTree = (SyntaxTree)tree.Clone();
            foreach (var entity in TopLevelTypes(componentTree))
            {
                if (entity.GetSymbol() is not ITypeDefinition definition || definition.FullName != component.Type.FullName)
                    entity.Remove();
            }
            // Assembly/module attributes belong to the central file exactly once. Type and
            // member attributes, scoped imports, and nested helper types stay with the class.
            foreach (var attribute in componentTree.Children.OfType<AttributeSection>().ToArray())
                attribute.Remove();
            RemoveEmptyNamespaces(componentTree);
            files.Add(new SourceFile(component.Path, componentTree.ToString(settings.CSharpFormattingOptions), component.Type.FullName));
        }
        foreach (var component in candidates)
            component.Declaration.Remove();
        RemoveEmptyNamespaces(tree);
        files.Insert(0, new SourceFile("Recovered.cs", tree.ToString(settings.CSharpFormattingOptions)));
        return files;
    }

    private static EntityDeclaration[] TopLevelTypes(SyntaxTree tree) => tree.Descendants.OfType<EntityDeclaration>()
        .Where(e => e.Parent is SyntaxTree or NamespaceDeclaration).ToArray();

    private static bool IsRepresentableIdentifier(string name)
    {
        if (name.Length == 0)
            return false;
        for (var i = 0; i < name.Length; i++)
        {
            var category = char.GetUnicodeCategory(name[i]);
            var start = name[i] == '_' || category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
                UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
            if (!start && (i == 0 || category is not (UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation or
                    UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)))
                return false;
        }
        // Keywords are preserved by the writer's @ escaping. Formatting characters are
        // deliberately rejected: C# removes them from identifier identity. Supplementary
        // characters stay unsupported until the exact Unity compiler establishes their use.
        return true;
    }

    private static void RemoveEmptyNamespaces(SyntaxTree tree)
    {
        foreach (var ns in tree.Descendants.OfType<NamespaceDeclaration>().Reverse().ToArray())
            if (!ns.Descendants.OfType<EntityDeclaration>().Any())
                ns.Remove();
    }

    private static bool IsUnityComponent(ITypeDefinition type) => type.GetAllBaseTypes().Any(baseType =>
        baseType.FullName is "UnityEngine.MonoBehaviour" or "UnityEngine.ScriptableObject" &&
        baseType.GetDefinition()?.ParentModule?.AssemblyName is "UnityEngine.CoreModule" or "UnityEngine");
}
