using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.CIL;
using Cpp2IL.Core.Extensions;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Logging;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Reporting;
using Cpp2IL.Core.Utils;

namespace Cpp2IL.Core.OutputFormats;

public class AsmResolverDllOutputFormatIlRecovery : AsmResolverDllOutputFormat
{
    private readonly ConcurrentDictionary<MethodAnalysisContext, MethodRecoveryResult> _methodResults = new();
    private int _inputTypeCount;

    public override string OutputFormatId => "dll_il_recovery";

    public override string OutputFormatName => "DLL files with IL Recovery";

    public bool RequireCompleteRecovery { get; set; }

    public RecoveryReport? LastRecoveryReport { get; private set; }

    public override void OnOutputFormatSelected()
    {
        if (Cpp2IlApi.RuntimeOptions != null)
            RequireCompleteRecovery = Cpp2IlApi.RuntimeOptions.StrictRecovery;
    }

    public override void DoOutput(ApplicationAnalysisContext context, string outputRoot)
    {
        LastRecoveryReport = null;
        try
        {
            base.DoOutput(context, outputRoot);
        }
        finally
        {
            // Strict rejection must still leave an inspectable report, even though no DLLs are written.
            LastRecoveryReport?.WriteJson(Path.Combine(outputRoot, "recovery-report.json"));
        }
    }

    public override List<AssemblyDefinition> BuildAssemblies(ApplicationAnalysisContext context)
    {
        BeginRecoveryReport(context);
        var completed = false;
        try
        {
            Logger.InfoNewline("Finding key function addresses...");
            var start = DateTime.Now;
            _ = context.GetOrCreateKeyFunctionAddresses();
            Logger.InfoNewline($"Key function addresses found in {DateTime.Now.Subtract(start).TotalMilliseconds}ms");

            var assemblies = base.BuildAssemblies(context);
            completed = true;
            return assemblies;
        }
        finally
        {
            LastRecoveryReport = CreateRecoveryReport(context, completed);
            Logger.InfoNewline($"IL recovery: {LastRecoveryReport.EmittedMethodCount} emitted without detected degradation, " +
                              $"{LastRecoveryReport.UnresolvedMethodCount} unresolved, {LastRecoveryReport.ExcludedMethodCount} excluded; " +
                              $"{LastRecoveryReport.InputMethodCount} input methods. Behavioral validation has not run.", "DllOutput");
            if (completed && RequireCompleteRecovery)
                LastRecoveryReport.EnsureComplete();
        }
    }

    protected void BeginRecoveryReport(ApplicationAnalysisContext context)
    {
        _methodResults.Clear();
        LastRecoveryReport = null;
        // The base class's old "successfully decompiled" counter is deliberately not used.
        TotalMethodCount = SuccessfulMethodCount = 0;
        _inputTypeCount = context.AllTypes.Count(t => t.Definition != null);
        RegisterMethods(context);
    }

    private void RegisterMethods(ApplicationAnalysisContext context)
    {
        var nextIdentity = 0;
        foreach (var assembly in context.Assemblies)
        foreach (var type in assembly.Types)
        foreach (var method in type.Methods)
        {
            if (_methodResults.ContainsKey(method))
                continue;
            if (_methodResults.TryAdd(method, new MethodRecoveryResult(nextIdentity,
                assembly.Name, type.FullName, method.Name, method.FullNameWithSignature, method.Token,
                method.Definition != null, method.UnderlyingPointer != 0,
                MethodRecoveryDisposition.NotProcessed, ["Method was not processed."])))
                nextIdentity++;
        }
    }

    protected RecoveryReport CreateRecoveryReport(ApplicationAnalysisContext context, bool completed)
        => new(_methodResults.Values, _inputTypeCount, context.UnityVersion.ToString(),
            context.Binary.InstructionSetId.ToString(), completed,
            context.Assemblies.Where(assembly => assembly.Definition != null).Select(assembly => assembly.Name));

    private void Record(MethodAnalysisContext method, MethodRecoveryDisposition disposition, params string[] reasons)
        => _methodResults[method] = _methodResults[method].WithDisposition(disposition, reasons.Concat(method.AnalysisWarnings));

    public static bool IsReferenceAssembly(string assemblyName)
    {
        var name = assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? assemblyName[..^4] : assemblyName;
        return name is "mscorlib" or "netstandard" or "System" or "UnityEngine" ||
               name.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
               name.StartsWith("Unity.", StringComparison.Ordinal) ||
               name.StartsWith("System.", StringComparison.Ordinal);
    }

    protected override void FillMethodBody(MethodDefinition methodDefinition, MethodAnalysisContext methodContext)
    {
        try
        {
            if (methodContext is InjectedMethodAnalysisContext)
            {
                if (methodDefinition.IsManagedMethodWithBody())
                    FillMethodBodyWithStub(methodDefinition);
                Record(methodContext, MethodRecoveryDisposition.ExcludedInjectedMethod, "Injected method has no original player body to recover.");
                return;
            }

            if (!methodDefinition.IsManagedMethodWithBody())
            {
                Record(methodContext, MethodRecoveryDisposition.NoManagedBody, "Declaration does not require a managed body (abstract, external, or runtime-provided).");
                return;
            }

            if (IsReferenceAssembly(methodContext.DeclaringType!.DeclaringAssembly.Name))
            {
                FillMethodBodyWithStub(methodDefinition);
                Record(methodContext, MethodRecoveryDisposition.ExcludedReferenceAssembly, "Reference assembly body excluded from application recovery; emitted body is a stub.");
                return;
            }

            if (methodContext.UnderlyingPointer == 0)
            {
                FillMethodBodyWithStub(methodDefinition);
                Record(methodContext, MethodRecoveryDisposition.NoNativeBody, "No native body address is available; emitted body is a stub.");
                return;
            }

            if (MethodAnalysisContext.MaxMethodSizeBytes != -1 && methodContext.RawBytes.Length > MethodAnalysisContext.MaxMethodSizeBytes)
            {
                FillMethodBodyWithStub(methodDefinition);
                Record(methodContext, MethodRecoveryDisposition.SkippedMethodSize, "Native body exceeds the configured analysis size limit; emitted body is a stub.");
                return;
            }

            if (X64CatchDivideRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Managed catch IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64FinallyCountRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Managed finally IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64GuardedFieldCallRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Guarded field-receiver call IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64CallResultInt32ArrayReadRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Call-result Int32 array read IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64GuardedEnumParameterCallRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Guarded unchanged-enum call IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64GuardedStructParameterCallRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Guarded eight-byte struct call IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64MetadataStaticGetterRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "TypeInfo-guarded static getter IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64MetadataStaticInt32SetterRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "TypeInfo-guarded static Int32 setter IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64InstanceReferenceSetterRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Instance reference setter IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64StructStaticConstructorRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Struct static constructor IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64InheritedInt32ConstructorRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Inherited Int32 constructor IL emitted from a complete shared native body, proved inert immediate-base thunk, and unchanged field layout; behavior remains unverified.");
                return;
            }

            if (X64AncestorConstructorThunkRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Immediate-base constructor call IL emitted from a complete shared native tail thunk and unchanged constructor chain; behavior remains unverified.");
                return;
            }

            if (X64GenericBaseConstructorRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Constructed generic base constructor call IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64GenericInterfaceWrapperRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Explicit interface generic MethodRef call IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64GuardedSinkCallRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Guarded external sink call IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64GuardedBaseConstructorRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Class-init guarded immediate base constructor call IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64MetadataStaticInt32AddRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "TypeInfo-guarded static Int32 addition IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64MetadataStaticObjectInt32AddRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "TypeInfo-guarded static and instance Int32 addition IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64ClassCastLookupRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Class-cast lookup IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64TypeFromHandleRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Type-from-handle IL emitted from complete bounded native, metadata, and class-init evidence; behavior remains unverified.");
                return;
            }

            if (X64NestedBooleanLiteralStoreRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Nested Boolean literal store IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64NestedSingleForwardStoreRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Nested Single setter call IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64NestedScalarFieldReadRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Nested scalar field read IL emitted from complete bounded native and metadata evidence; behavior remains unverified.");
                return;
            }

            if (X64ScalarFloatRefMutationRecovery.TryGenerate(methodContext, methodDefinition))
            {
                Record(methodContext, MethodRecoveryDisposition.Emitted,
                    "Scalar-float byref mutation IL emitted from complete native, ABI, and metadata evidence; behavior remains unverified.");
                return;
            }

            methodContext.Analyze();

            if (methodContext.ConvertedIsil.Count == 0)
            {
                FillMethodBodyWithStub(methodDefinition);
                Record(methodContext, MethodRecoveryDisposition.EmptyAnalysis, "Native body produced no analyzed instructions; emitted body is a stub.");
                return;
            }

            IlGenerator.GenerateIl(methodContext, methodDefinition);
            Record(methodContext, methodContext.AnalysisWarnings.Count == 0
                    ? MethodRecoveryDisposition.Emitted : MethodRecoveryDisposition.Partial,
                methodContext.AnalysisWarnings.Count == 0
                    ? "Managed IL emitted without detected degradation; behavior remains unverified."
                    : "Managed IL emitted with unresolved analysis warnings.");
        }
        catch (Exception e)
        {
            // Known analysis limitations (DecompilerException) get a one-line warning; anything
            // else is an unexpected bug and keeps its (collapsed) stack trace.
            var detail = e is DecompilerException ? e.Message : e.ToCollapsedString();

            if (detail.Length > 1000) // unbounded ldstrs can overflow the 24 bit #US heap offset space
                detail = detail[..1000] + "…";

            if (e is DecompilerException)
                Logger.WarnNewline($"Skipping {methodContext.FullName}: {e.Message}");
            else
                Logger.ErrorNewline($"Decompiling {methodContext.FullName} failed: {detail}");

            Record(methodContext, MethodRecoveryDisposition.Failed, e.GetType().Name + ": " + detail);
            methodDefinition.CilMethodBody = new();
            var instructions = methodDefinition.CilMethodBody.Instructions;

            var module = methodDefinition.DeclaringModule!;
            var factory = module.CorLibTypeFactory;
            var exceptionCtor = factory.CorLibScope
                .CreateTypeReference("System", "Exception")
                .CreateMemberReference(".ctor", MethodSignature.CreateInstance(factory.Void, [factory.String]));

            instructions.Add(CilOpCodes.Ldstr, detail);
            instructions.Add(CilOpCodes.Newobj, exceptionCtor);
            instructions.Add(CilOpCodes.Throw);
        }
        finally
        {
            methodContext.ReleaseAnalysisData();
        }
    }

    public static void WriteControlFlowGraph(MethodAnalysisContext method, string outputPath)
    {
        var graph = method.ControlFlowGraph;

        var sb = new StringBuilder();
        var edges = new List<(int, int)>();

        sb.AppendLine("digraph ControlFlowGraph {");
        sb.AppendLine("    \"label\"=\"Control flow graph\"");

        // no instructions
        graph ??= new ISILControlFlowGraph([]);

        var methodText = $@"{CsFileUtils.GetKeyWordsForMethod(method)} {method.FullNameWithSignature}
parameter locals: {string.Join(", ", method.ParameterLocals)}
parameter operands: {string.Join(", ", method.ParameterOperands)}";

        foreach (var block in graph.Blocks)
        {
            if (block == graph.EntryBlock || block == graph.ExitBlock)
            {
                var isEntry = block == graph.EntryBlock;
                sb.AppendLine($"""
                               	{block.ID} [
                               		"color"="{(isEntry ? "green" : "red")}"
                               		"label"="{(isEntry ? $"Entry ({block.ID})\n{methodText}" : $"Exit ({block.ID})")}"
                               	]
                               """);
            }
            else
            {
                sb.AppendLine($"""
                               	{block.ID} [
                               		"shape"="box"
                               		"label"="{block.ToString().EscapeString().Replace("\\r", "")}"
                               	]
                               """);
            }

            edges.AddRange(block.Successors.Select(b => (block.ID, b.ID)));
        }

        foreach (var edge in edges)
            sb.AppendLine($"    {edge.Item1} -> {edge.Item2}");

        sb.AppendLine("}");

        var type = method.DeclaringType!;
        var assemblyName = MiscUtils.CleanPathElement(type.DeclaringAssembly.CleanAssemblyName);
        var typePath = Path.Combine(type.FullName.Split('.').Select(MiscUtils.CleanPathElement).ToArray());
        var directoryPath = Path.Combine(outputPath, assemblyName, typePath);

        var methodName = MiscUtils.CleanPathElement(method.Name + "_" + string.Join("_",
            method.Parameters.Select(p => MiscUtils.CleanPathElement(p.ParameterType.Name))));
        var path = Path.Combine(directoryPath, methodName) + ".dot";

        if (path.Length > 260)
        {
            path = path[..250];
            path += ".dot";
        }

        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, sb.ToString());
    }
}
