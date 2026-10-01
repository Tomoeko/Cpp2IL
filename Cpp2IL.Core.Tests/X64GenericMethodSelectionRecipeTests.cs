using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.PE;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64GenericMethodSelectionRecipeTests
{
    [Test]
    public void NativeSelectionPreservesLookupOrderKeysResultsAndWin64Abi()
    {
        const string environment = "CPP2IL_NATIVE_DIRECT_GENERIC_REFERENCE_INVOCATION_FIXTURE_INPUT";
        var input = Environment.GetEnvironmentVariable(environment);
        if (string.IsNullOrEmpty(input))
            Assert.Ignore("Set " + environment + " to the neutral exact player-input directory.");

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(input!, "GameAssembly.dll"),
                Directory.EnumerateFiles(input!, "global-metadata.dat", SearchOption.AllDirectories).Single(),
                UnityVersion.Parse("2021.3.35f1"));
            var (entry, body) = FindOriginalRecipe(Cpp2IlApi.CurrentAppContext!);
            var results = X64GenericMethodSelectionControls.Run(entry, body);
            Assert.Multiple(() =>
            {
                Assert.That(results.InterpretedCases, Is.EqualTo(210));
                Assert.That(results.ShapeAndFreshnessControls, Is.EqualTo(25));
                Assert.That(results.CompleteHelperQualified, Is.False);
            });
            TestContext.Out.WriteLine("210 native-instruction selector cases and 25 mutation/freshness/ABI controls passed.");
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }

    // Discover the neutral fixture's helper through original function boundaries.
    // No player addresses, symbols or byte fingerprints are part of this test.
    private static (ulong Entry, byte[] Body) FindOriginalRecipe(ApplicationAnalysisContext app)
    {
        var pe = (PE)app.Binary;
        var index = X64UnwindProof.ForApplication(app)
            ?? throw new InvalidOperationException("Original PE unwind evidence is unavailable.");
        var image = pe.GetRawBinaryContent();
        var header = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(0x3c, 4));
        var optional = checked(header + 24);
        if (BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(optional, 2)) != 0x20b)
            throw new InvalidOperationException("The fixture is not PE x64.");
        var directory = checked(optional + 112 + 3 * 8);
        var tableRva = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(directory, 4));
        var tableSize = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(directory + 4, 4));
        var table = checked((int)pe.MapVirtualAddressToRaw(index.ImageBase + tableRva, false));
        if (tableSize % 12 != 0 || table < 0 || tableSize > image.Length - (long)table)
            throw new InvalidOperationException("Original function-table bounds are invalid.");

        var candidates = new List<(ulong Entry, byte[] Body)>();
        for (var ordinal = 0; ordinal < tableSize / 12; ordinal++)
        {
            var row = image.Slice(checked(table + ordinal * 12), 12);
            var first = BinaryPrimitives.ReadUInt32LittleEndian(row);
            var last = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(4));
            if (last <= first || last - first != 396) continue;
            var entry = checked(index.ImageBase + first);
            var end = checked(index.ImageBase + last);
            var span = index.ClassifySpan(entry, end);
            if (span.Kind != X64UnwindProof.SpanKind.HandlerFree || span.Start != entry ||
                span.End != end || span.RootStart != entry) continue;
            var offset = pe.MapVirtualAddressToRaw(entry, false);
            if (offset < 0 || offset > image.Length - 396) continue;
            var body = image.Slice(checked((int)offset), 396).ToArray();
            if (X64AncestorConstructorThunkProof.FileBackedExecutable(pe, index, body, entry) &&
                X64GenericMethodSelectionRecipe.Evidence.Identify(entry, body) != null)
                candidates.Add((entry, body));
        }
        return candidates.Single();
    }
}
