using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Cpp2IL.Core.Reporting;

/// <summary>
/// A snapshot of every managed method considered by an output run. Emission is not behavioral verification.
/// Runtime reports contain input identifiers and must be kept with the input's private artifacts.
/// </summary>
public sealed class RecoveryReport
{
    public int SchemaVersion { get; private set; } = 1;
    public string UnityVersion { get; private set; }
    public string InstructionSet { get; private set; }
    public string Scope { get; private set; } = "Application managed method bodies; reference assemblies and bodyless declarations are explicitly excluded.";
    public int InputTypeCount { get; private set; }
    public int InputMethodCount { get; private set; }
    public string[] InputAssemblyNames { get; private set; }
    public int InputMethodsWithNativeBodies { get; private set; }
    public int InjectedMethodCount { get; private set; }
    public int EmittedMethodCount { get; private set; }
    public int UnresolvedMethodCount { get; private set; }
    public int ExcludedMethodCount { get; private set; }
    public Dictionary<string, int> DispositionCounts { get; private set; }
    public string BehavioralValidation { get; private set; } = "NotRun";
    public string UnityCompilation { get; private set; } = "NotRun";
    public string NativePlayerBuild { get; private set; } = "NotRun";
    public int BehaviorallyVerifiedMethodCount { get; private set; }
    public bool AnalysisCompleted { get; private set; }
    public MethodRecoveryResult[] Methods { get; private set; }

    public RecoveryReport(IEnumerable<MethodRecoveryResult> methods, int inputTypeCount,
        string unityVersion, string instructionSet, bool analysisCompleted)
        : this(methods, inputTypeCount, unityVersion, instructionSet, analysisCompleted, null)
    {
    }

    public RecoveryReport(IEnumerable<MethodRecoveryResult> methods, int inputTypeCount,
        string unityVersion, string instructionSet, bool analysisCompleted, IEnumerable<string>? inputAssemblyNames)
    {
        Methods = methods.OrderBy(m => m.Identity).ToArray();
        if (Methods.Select(m => m.Identity).Distinct().Count() != Methods.Length)
            throw new ArgumentException("Recovery report method identities must be unique.", nameof(methods));

        InputTypeCount = inputTypeCount;
        InputAssemblyNames = (inputAssemblyNames ?? Methods.Where(m => m.IsInputMethod).Select(m => m.AssemblyName))
            .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        UnityVersion = unityVersion;
        InstructionSet = instructionSet;
        AnalysisCompleted = analysisCompleted;
        InputMethodCount = Methods.Count(m => m.IsInputMethod);
        InputMethodsWithNativeBodies = Methods.Count(m => m.IsInputMethod && m.HasNativeBody);
        InjectedMethodCount = Methods.Length - InputMethodCount;
        EmittedMethodCount = Methods.Count(m => m.Disposition == MethodRecoveryDisposition.Emitted);
        UnresolvedMethodCount = Methods.Count(m => m.IsUnresolved);
        ExcludedMethodCount = Methods.Count(m => m.IsExcluded);
        MethodRecoveryDisposition[] dispositions = [MethodRecoveryDisposition.NotProcessed, MethodRecoveryDisposition.Emitted,
            MethodRecoveryDisposition.Partial, MethodRecoveryDisposition.Failed, MethodRecoveryDisposition.NoNativeBody,
            MethodRecoveryDisposition.EmptyAnalysis, MethodRecoveryDisposition.SkippedMethodSize,
            MethodRecoveryDisposition.ExcludedReferenceAssembly, MethodRecoveryDisposition.NoManagedBody,
            MethodRecoveryDisposition.ExcludedInjectedMethod];
        DispositionCounts = dispositions
            .ToDictionary(d => d.ToString(), d => Methods.Count(m => m.Disposition == d), StringComparer.Ordinal);
    }

    /// <summary>
    /// Rejects detected recovery gaps. Passing this check does not establish behavioral equivalence,
    /// full managed type verification, Unity compilation, or native build success.
    /// </summary>
    public void EnsureComplete(IEnumerable<string>? assemblyNames = null)
    {
        var selected = Methods.AsEnumerable();
        if (assemblyNames != null)
        {
            var names = new HashSet<string>(assemblyNames, StringComparer.Ordinal);
            if (names.Count == 0 || names.Any(n => !InputAssemblyNames.Contains(n, StringComparer.Ordinal)))
                throw new IncompleteRecoveryException("Strict recovery requires a nonempty selection of assemblies present in the report.", this);
            selected = selected.Where(m => names.Contains(m.AssemblyName));
        }

        var scopedMethods = selected.Where(m => !m.IsExcluded).ToArray();
        var unresolved = scopedMethods.Count(m => m.IsUnresolved);
        // Explicitly selected input assemblies may contain only interfaces, enums or other
        // declarations requiring no managed bodies. This establishes no recovered behavior.
        // A reference/injected-method exclusion is not such a declaration-only proof.
        var declarationOnly = assemblyNames != null && selected.All(m =>
            m.IsInputMethod && m.Disposition == MethodRecoveryDisposition.NoManagedBody);
        if (!AnalysisCompleted || (scopedMethods.Length == 0 && !declarationOnly) || unresolved != 0)
            throw new IncompleteRecoveryException($"Strict recovery rejected output: {unresolved} unresolved method(s) in {scopedMethods.Length} scoped method(s); analysis completed: {AnalysisCompleted}. See the recovery report. Emission is not behavioral verification.", this);
    }

    public void WriteJson(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var json = new StringBuilder("{\n");
        void Text(string name, string value) => json.Append(JsonText.Quote(name)).Append(':').Append(JsonText.Quote(value)).Append(",\n");
        void Number(string name, int value) => json.Append(JsonText.Quote(name)).Append(':').Append(value.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        Number(nameof(SchemaVersion), SchemaVersion);
        Text(nameof(UnityVersion), UnityVersion);
        Text(nameof(InstructionSet), InstructionSet);
        Text(nameof(Scope), Scope);
        Number(nameof(InputTypeCount), InputTypeCount);
        Number(nameof(InputMethodCount), InputMethodCount);
        json.Append(JsonText.Quote(nameof(InputAssemblyNames))).Append(':').Append(JsonText.Array(InputAssemblyNames)).Append(",\n");
        Number(nameof(InputMethodsWithNativeBodies), InputMethodsWithNativeBodies);
        Number(nameof(InjectedMethodCount), InjectedMethodCount);
        Number(nameof(EmittedMethodCount), EmittedMethodCount);
        Number(nameof(UnresolvedMethodCount), UnresolvedMethodCount);
        Number(nameof(ExcludedMethodCount), ExcludedMethodCount);
        json.Append("\"DispositionCounts\":{")
            .Append(string.Join(",", DispositionCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => JsonText.Quote(pair.Key) + ":" + pair.Value.ToString(CultureInfo.InvariantCulture))))
            .Append("},\n");
        Text(nameof(BehavioralValidation), BehavioralValidation);
        Text(nameof(UnityCompilation), UnityCompilation);
        Text(nameof(NativePlayerBuild), NativePlayerBuild);
        Number(nameof(BehaviorallyVerifiedMethodCount), BehaviorallyVerifiedMethodCount);
        json.Append("\"AnalysisCompleted\":").Append(AnalysisCompleted ? "true" : "false").Append(",\n");
        json.Append("\"Methods\":[\n").Append(string.Join(",\n", Methods.Select(m => m.ToJson()))).Append("\n]}\n");
        File.WriteAllText(path, json.ToString(), new UTF8Encoding(false));
    }
}
