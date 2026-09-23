using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Cpp2IL.Core.Reporting;

namespace Cpp2IL.Core.Tests;

public class RecoveryReportTests
{
    [Test]
    public void ReportKeepsEveryMethodAndDoesNotLabelEmissionAsVerifiedBehavior()
    {
        var dispositions = Enum.GetValues<MethodRecoveryDisposition>();
        var report = Report(dispositions.Select((d, i) => Result(i, d)));

        Assert.Multiple(() =>
        {
            Assert.That(report.InputMethodCount, Is.EqualTo(dispositions.Length));
            Assert.That(report.Methods, Has.Length.EqualTo(dispositions.Length));
            Assert.That(report.DispositionCounts.Values.Sum(), Is.EqualTo(dispositions.Length));
            Assert.That(report.EmittedMethodCount, Is.EqualTo(1));
            Assert.That(report.ExcludedMethodCount, Is.EqualTo(3));
            Assert.That(report.UnresolvedMethodCount, Is.EqualTo(dispositions.Length - 4));
            Assert.That(report.BehaviorallyVerifiedMethodCount, Is.Zero);
            Assert.That(report.BehavioralValidation, Is.EqualTo("NotRun"));
        });
        Assert.Throws<IncompleteRecoveryException>(() => report.EnsureComplete());
    }

    [Test]
    public void StrictSelectionRejectsGapsWithoutDiscardingOtherAssemblyResults()
    {
        var report = Report([
            Result(0, MethodRecoveryDisposition.Emitted, "Complete"),
            Result(1, MethodRecoveryDisposition.Partial, "Incomplete"),
            Result(2, MethodRecoveryDisposition.ExcludedReferenceAssembly, "Reference"),
        ]);

        Assert.DoesNotThrow(() => report.EnsureComplete(["Complete"]));
        Assert.Throws<IncompleteRecoveryException>(() => report.EnsureComplete());
        Assert.Throws<IncompleteRecoveryException>(() => report.EnsureComplete(["Incomplete"]));
        Assert.Throws<IncompleteRecoveryException>(() => report.EnsureComplete(["Missing"]));
        Assert.Throws<IncompleteRecoveryException>(() => report.EnsureComplete(["Reference"]));
        Assert.Throws<IncompleteRecoveryException>(() => report.EnsureComplete([]));
        Assert.That(report.Methods, Has.Length.EqualTo(3));
    }

    [Test]
    public void StrictRecoveryRejectsInterruptedAndEmptyRuns()
    {
        var interrupted = new RecoveryReport([Result(0, MethodRecoveryDisposition.Emitted)], 1, "test", "test", false);
        Assert.Throws<IncompleteRecoveryException>(() => interrupted.EnsureComplete());
        Assert.Throws<IncompleteRecoveryException>(() => Report([]).EnsureComplete());
    }

    [Test]
    public void JsonRetainsExplicitDispositionAndEscapesInputIdentifiers()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "recovery-report-test.json");
        try
        {
            Report([Result(0, MethodRecoveryDisposition.Partial, "Sample\"\\\nAssembly")]).WriteJson(path);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var method = json.RootElement.GetProperty("Methods")[0];
            Assert.Multiple(() =>
            {
                Assert.That(method.GetProperty("AssemblyName").GetString(), Is.EqualTo("Sample\"\\\nAssembly"));
                Assert.That(method.GetProperty("Disposition").GetString(), Is.EqualTo("Partial"));
                Assert.That(method.GetProperty("ManagedIlValidation").GetString(), Is.EqualTo("LabelsAndStackDepthChecked"));
                Assert.That(json.RootElement.GetProperty("BehavioralValidation").GetString(), Is.EqualTo("NotRun"));
                Assert.That(json.RootElement.GetProperty("DispositionCounts").GetProperty("Partial").GetInt32(), Is.EqualTo(1));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestCase("\"\\\0\b\f\n\r\t")]
    [TestCase("Unicode \u03bb \ud83d\ude00")]
    public void JsonStringsRoundTripControlsAndUnicode(string value)
    {
        using var json = JsonDocument.Parse(JsonText.Quote(value));
        Assert.That(json.RootElement.GetString(), Is.EqualTo(value));
    }

    [Test]
    public void JsonStringsEscapeUnpairedSurrogatesWithoutWritingInvalidUtf8()
        => Assert.That(JsonText.Quote("\ud800"), Is.EqualTo("\"\\ud800\""));

    private static MethodRecoveryResult Result(int identity, MethodRecoveryDisposition disposition, string assembly = "Sample")
        => new(identity, assembly, "SampleType", "Method", "System.Void Method()", 1, true, true, disposition);

    private static RecoveryReport Report(System.Collections.Generic.IEnumerable<MethodRecoveryResult> methods)
        => new(methods, 1, "2021.3.35f1", "X86_64", true);
}
