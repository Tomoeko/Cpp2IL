using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using AssetRipper.Primitives;
using LibCpp2IL;
using Xunit;

namespace LibCpp2ILTests;

public class Tests
{
    private const string ExternalSampleOptIn = "CPP2IL_RUN_EXTERNAL_PARSER_TESTS";
    private const string ExternalSampleSkipReason = "External parser samples require CPP2IL_RUN_EXTERNAL_PARSER_TESTS=1; local synthetic fixtures run offline.";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly ITestOutputHelper _outputHelper;

    public static bool ExternalSamplesEnabled => Environment.GetEnvironmentVariable(ExternalSampleOptIn) == "1";

    public Tests(ITestOutputHelper outputHelper)
    {
        _outputHelper = outputHelper;

        //Configure the lib.
        LibCpp2IlMain.Settings.DisableGlobalResolving = true;
        LibCpp2IlMain.Settings.DisableMethodPointerMapping = true;
        LibCpp2IlMain.Settings.AllowManualMetadataAndCodeRegInput = false;
    }

    [Theory]
    [InlineData("Simple_2019_4_34", "2019.4.34f1", 24.5f)]
    [InlineData("Simple_2022_3_35", "2022.3.35f1", 31.1f)]
    [InlineData("Simple_6000_5_0_a6", "6000.5.0a6", 106f)]
    public async Task LocalSyntheticPlayerLoads(string fixture, string unityVersion, float expectedMetadataVersion)
    {
        var root = Path.Combine(FindRepositoryRoot(), "TestFiles", fixture);
        var metadataPath = Path.Combine(root, fixture + "_Data", "il2cpp_data", "Metadata", "global-metadata.dat");
        var binaryPath = Path.Combine(root, "GameAssembly.dll");
        var metadataBytes = await File.ReadAllBytesAsync(metadataPath, TestContext.Current.CancellationToken);
        var binaryBytes = await File.ReadAllBytesAsync(binaryPath, TestContext.Current.CancellationToken);

        var context = CheckFiles(metadataBytes, binaryBytes, UnityVersion.Parse(unityVersion));
        Assert.Equal(expectedMetadataVersion, context.Metadata.MetadataVersion);
        Assert.Equal(8, context.Binary.PointerSizeBytes);
        Assert.NotEmpty(context.Metadata.typeDefs);
        Assert.NotEmpty(context.Metadata.methodDefs);
    }

    [Fact(Skip = ExternalSampleSkipReason, SkipUnless = nameof(ExternalSamplesEnabled))]
    public Task Metadata24_1_64BitSupportIsPresent() => CheckExternalFiles("metadata-24-1-x64", "meta_24.1_x64.dat", "GA_24.1_x64.dll", new UnityVersion(2018, 4, 20));

    [Fact(Skip = ExternalSampleSkipReason, SkipUnless = nameof(ExternalSamplesEnabled))]
    public Task Metadata24_3_64BitSupportIsPresent() => CheckExternalFiles("metadata-24-3-x64", "meta_24.3_x64.dat", "GA_24.3_x64.dll", new UnityVersion(2019, 4, 11));

    [Fact(Skip = ExternalSampleSkipReason, SkipUnless = nameof(ExternalSamplesEnabled))]
    public Task Metadata24_3_ARM32ElfSupportIsPresent() => CheckExternalFiles("metadata-24-3-arm32", "meta_24.3_arm32.dat", "GA_24.3_arm32.so", new UnityVersion(2019, 4, 20));

    [Fact(Skip = ExternalSampleSkipReason, SkipUnless = nameof(ExternalSamplesEnabled))]
    public Task Metadata27_1_32BitSupportIsPresent() => CheckExternalFiles("metadata-27-1-x32", "meta_27.1_x32.dat", "GA_27.1_x32.dll", new UnityVersion(2020, 2, 6));

    [Fact(Skip = ExternalSampleSkipReason, SkipUnless = nameof(ExternalSamplesEnabled))]
    public Task Metadata27_1_AARCH64ElfSupportIsPresent() => CheckExternalFiles("metadata-27-1-arm64", "meta_27.1_aarch64.dat", "GA_27.1_aarch64.so", new UnityVersion(2020, 2, 6));

    private async Task CheckExternalFiles(string fixture, string metadataName, string binaryName, UnityVersion unityVersion)
    {
        // Guard the download path too: direct calls cannot bypass the explicit network opt-in.
        if (!ExternalSamplesEnabled)
            throw new InvalidOperationException(ExternalSampleSkipReason);

        var directory = Path.Combine(FindRepositoryRoot(), "Files", "parser-samples", fixture);
        var metadataBytes = await ReadOrDownload(directory, metadataName);
        var binaryBytes = await ReadOrDownload(directory, binaryName);
        CheckFiles(metadataBytes, binaryBytes, unityVersion);
    }

    private async Task<byte[]> ReadOrDownload(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (File.Exists(path))
            return await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        _outputHelper.WriteLine($"Downloading optional parser sample: {fileName}");
        var bytes = await Client.GetByteArrayAsync("http://samboycoding.me/static/" + fileName, TestContext.Current.CancellationToken);
        Assert.NotEmpty(bytes);
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".download";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, TestContext.Current.CancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
        return bytes;
    }

    private LibCpp2IlContext CheckFiles(byte[] metadataBytes, byte[] binaryBytes, UnityVersion unityVersion)
    {
        _outputHelper.WriteLine($"Parsing synthetic/sample input for Unity {unityVersion}.");
        var context = LibCpp2IlContextBuilder.Build(binaryBytes, metadataBytes, unityVersion);
        Assert.NotNull(context);
        Assert.NotNull(context.Binary);
        Assert.NotNull(context.Metadata);
        Assert.Equal(unityVersion, context.Metadata.UnityVersion);
        return context;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Cpp2IL.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Cannot find the repository containing Cpp2IL.slnx and the tracked TestFiles fixtures.");
    }
}
