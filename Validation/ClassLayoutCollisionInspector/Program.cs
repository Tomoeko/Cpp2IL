using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using AssetRipper.Primitives;
using Cpp2IL.Core;
using LibCpp2IL;
using LibCpp2IL.PE;

namespace ClassLayoutCollisionInspector;

internal static class Program
{
    private const string AssemblyName = "ClassLayoutCollisionFixture";
    private static readonly LayoutExpectation[] Expectations =
    [
        new("ImplicitSize", "ValueType", 0),
        new("ExplicitNaturalSize", "ValueType", 6),
        new("ExplicitLargerSize", "ValueType", 8),
        new("ImplicitClassSize", "Object", 0),
        new("ExplicitNaturalClassSize", "Object", 6),
        new("ExplicitLargerClassSize", "Object", 32),
    ];

    private static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("Usage: ClassLayoutCollisionInspector PLAYER_INPUT STRIPPED_DLL UNSTRIPPED_DLL OUTPUT_JSON");
            return 2;
        }

        try
        {
            var output = Path.GetFullPath(args[3]);
            RequireIgnoredOutput(output);
            var player = Path.GetFullPath(args[0]);
            var binary = Path.Combine(player, "GameAssembly.dll");
            var metadata = Path.Combine(player, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
            if (!File.Exists(binary) || !File.Exists(metadata))
                throw new ArgumentException("The player input is missing the exact native binary or metadata file.");

            var stripped = ReadManagedLayouts(args[1]);
            var unstripped = ReadManagedLayouts(args[2]);
            ValidateManagedLayouts(stripped);
            ValidateManagedLayouts(unstripped);
            if (stripped.Where((layout, index) => layout.PackingSize != unstripped[index].PackingSize ||
                    layout.DeclaredSize != unstripped[index].DeclaredSize ||
                    layout.TypeFlags != unstripped[index].TypeFlags ||
                    layout.BaseTypeName != unstripped[index].BaseTypeName ||
                    !layout.Fields.SequenceEqual(unstripped[index].Fields)).Any())
                throw new InvalidOperationException("Stripping changed the fixture's managed ClassLayout or fields.");
            Cpp2IlApi.Init();
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            try
            {
                var app = Cpp2IlApi.CurrentAppContext!;
                if (app.UnityVersion.ToString() != "2021.3.35f1" || app.MetadataVersion != 29f ||
                    app.Binary is not PE || app.Binary.InstructionSetId != DefaultInstructionSets.X86_64 ||
                    app.Binary.PointerSizeBytes != 8)
                    throw new InvalidOperationException("Player input is not Unity 2021.3.35f1 Windows x64 metadata version 29.");

                var assembly = app.GetAssemblyByName(AssemblyName) ?? throw new InvalidOperationException("Fixture assembly is absent.");
                var native = Expectations.Select(expectation =>
                {
                    var type = assembly.Types.Single(candidate => candidate.Name == expectation.Name);
                    var definition = type.Definition ?? throw new InvalidOperationException("A fixture type lacks its native definition.");
                    var sizes = definition.RawSizes;
                    return new NativeLayout(expectation.Name, definition.Bitfield, definition.ClassSizeIsDefault,
                        definition.PackingSizeIsDefault, definition.PackingSize, definition.SpecifiedPackingSize,
                        sizes.native_size, sizes.instance_size,
                        type.Fields.Select(field =>
                        {
                            var rawType = field.BackingData?.Field.RawFieldType;
                            return new NativeField(field.Name, rawType?.Type.ToString() ?? "unknown", rawType?.Attrs ?? 0,
                                field.Offset);
                        }).ToArray());
                }).ToArray();

                var implicitLayout = native.Single(layout => layout.Name == "ImplicitSize");
                var naturalLayout = native.Single(layout => layout.Name == "ExplicitNaturalSize");
                var largerLayout = native.Single(layout => layout.Name == "ExplicitLargerSize");
                if (largerLayout.NativeSize <= naturalLayout.NativeSize)
                    throw new InvalidOperationException("The explicit larger-size control did not increase the native size.");
                var collision = SamePlayerFacts(implicitLayout, naturalLayout);
                var implicitClass = native.Single(layout => layout.Name == "ImplicitClassSize");
                var naturalClass = native.Single(layout => layout.Name == "ExplicitNaturalClassSize");
                var largerClass = native.Single(layout => layout.Name == "ExplicitLargerClassSize");
                if (largerClass.NativeSize <= naturalClass.NativeSize)
                    throw new InvalidOperationException("The explicit larger-class-size control did not increase the native size.");
                var classCollision = SamePlayerFacts(implicitClass, naturalClass);
                var report = new
                {
                    scope = "Exact-target player metadata and original managed ClassLayout; no recovered-source or behavior claim.",
                    unityVersion = app.UnityVersion.ToString(),
                    metadataVersion = app.MetadataVersion,
                    target = "Windows x64 IL2CPP",
                    native,
                    stripped,
                    unstripped,
                    omittedAndExplicitNaturalSizeHaveSamePlayerFacts = collision,
                    omittedAndExplicitNaturalClassSizeHaveSamePlayerFacts = classCollision,
                };
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
                Console.WriteLine($"Value type size collision: {collision}; reference class size collision: {classCollision}.");
                return 0;
            }
            finally
            {
                Cpp2IlApi.ResetInternalState();
            }
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or BadImageFormatException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static bool SamePlayerFacts(NativeLayout left, NativeLayout right) =>
        left.Bitfield == right.Bitfield && left.ClassSizeIsDefault == right.ClassSizeIsDefault &&
        left.PackingSizeIsDefault == right.PackingSizeIsDefault && left.PackingSize == right.PackingSize &&
        left.SpecifiedPackingSize == right.SpecifiedPackingSize && left.NativeSize == right.NativeSize &&
        left.InstanceSize == right.InstanceSize && left.Fields.SequenceEqual(right.Fields);

    private static void ValidateManagedLayouts(ManagedLayout[] layouts)
    {
        for (var index = 0; index < layouts.Length; index++)
        {
            var layout = layouts[index];
            var expected = Expectations[index];
            if (layout.Name != expected.Name || layout.BaseTypeName != expected.BaseTypeName ||
                layout.PackingSize != 2 || layout.DeclaredSize != expected.DeclaredSize ||
                layout.Fields.Length != 3 || !layout.Fields.SequenceEqual(layouts[0].Fields))
                throw new InvalidOperationException("The fixture's managed layout or field declarations differ from the controlled cases.");
        }
    }

    private static ManagedLayout[] ReadManagedLayouts(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var assembly = reader.GetAssemblyDefinition();
        if (reader.GetString(assembly.Name) != AssemblyName)
            throw new ArgumentException("Managed input has the wrong fixture assembly identity.");
        return Expectations.Select(expectation =>
        {
            var handle = reader.TypeDefinitions.Single(candidate =>
            {
                var type = reader.GetTypeDefinition(candidate);
                return reader.GetString(type.Namespace) == AssemblyName && reader.GetString(type.Name) == expectation.Name;
            });
            var definition = reader.GetTypeDefinition(handle);
            var layout = definition.GetLayout();
            var baseType = definition.BaseType.Kind switch
            {
                HandleKind.TypeReference => reader.GetString(reader.GetTypeReference(
                    (TypeReferenceHandle)definition.BaseType).Name),
                HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition(
                    (TypeDefinitionHandle)definition.BaseType).Name),
                _ => "unknown",
            };
            var fields = definition.GetFields().Select(fieldHandle =>
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                return new ManagedField(reader.GetString(field.Name), (int)field.Attributes,
                    Convert.ToHexString(reader.GetBlobBytes(field.Signature)));
            }).ToArray();
            return new ManagedLayout(expectation.Name, (int)definition.Attributes, baseType,
                layout.PackingSize, layout.Size, fields);
        }).ToArray();
    }

    private static void RequireIgnoredOutput(string output)
    {
        var root = Directory.GetCurrentDirectory();
        if (!Directory.Exists(Path.Combine(root, "LibCpp2IL")))
            throw new ArgumentException("Run the inspector from the repository root.");
        var privateRoot = Path.Combine(root, "Files") + Path.DirectorySeparatorChar;
        if (!output.StartsWith(privateRoot, StringComparison.Ordinal) || File.Exists(output))
            throw new ArgumentException("OUTPUT_JSON must be a new path inside this repository's ignored Files/ directory.");
        using var process = Process.Start(new ProcessStartInfo("git")
        {
            ArgumentList = { "check-ignore", "--quiet", output },
            UseShellExecute = false,
        })!;
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new ArgumentException("OUTPUT_JSON must be gitignored.");
    }

    private sealed record ManagedField(string Name, int Flags, string Signature);
    private sealed record LayoutExpectation(string Name, string BaseTypeName, int DeclaredSize);
    private sealed record ManagedLayout(string Name, int TypeFlags, string BaseTypeName,
        int PackingSize, int DeclaredSize, ManagedField[] Fields);
    private sealed record NativeField(string Name, string Type, uint Attributes, int Offset);
    private sealed record NativeLayout(string Name, uint Bitfield, bool ClassSizeIsDefault, bool PackingSizeIsDefault,
        uint PackingSize, uint SpecifiedPackingSize, int NativeSize, uint InstanceSize, NativeField[] Fields);
}
