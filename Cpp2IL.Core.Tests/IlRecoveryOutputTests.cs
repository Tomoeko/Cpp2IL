using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Reporting;
using ReflectionMethodAttributes = System.Reflection.MethodAttributes;
using ReflectionMethodImplAttributes = System.Reflection.MethodImplAttributes;

namespace Cpp2IL.Core.Tests;

public class IlRecoveryOutputTests
{
    [Test]
    public void FallbacksAndSharedAddressesRetainIndependentDispositions()
    {
        Cpp2IlApi.ResetInternalState();
        var app = TestGameLoader.LoadSimple2019Game();
        var assembly = app.InjectAssembly("RecoveryFixture");
        var type = assembly.InjectType("Fixture", "Sample", app.SystemTypes.SystemObjectType,
            System.Reflection.TypeAttributes.Public);
        var module = new ModuleDefinition("RecoveryFixture.dll", new AssemblyReference("mscorlib", new Version(4, 0, 0, 0)));
        var managedType = new TypeDefinition("Fixture", "Sample", AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public);
        module.TopLevelTypes.Add(managedType);

        var methods = new[]
        {
            new SyntheticNativeMethod(type, "MissingNative", 0, []),
            new SyntheticNativeMethod(type, "Empty", 1, []),
            new SyntheticNativeMethod(type, "Oversized", 1, []) { RawBytes = new BinarySlice(new byte[MethodAnalysisContext.MaxMethodSizeBytes + 1]) },
            new SyntheticNativeMethod(type, "Unsupported", 1, [new Instruction(0, OpCode.NotImplemented, new StringLiteral("synthetic"))]),
            new SyntheticNativeMethod(type, "Emitted", 1, [new Instruction(0, OpCode.Return)]),
            new SyntheticNativeMethod(type, "Partial", 1, [new Instruction(0, OpCode.Return)]) { AnalysisWarnings = ["Synthetic unresolved analysis warning."] },
        };
        type.Methods.AddRange(methods);
        var output = new InspectableRecoveryOutput();
        output.Begin(app);
        foreach (var method in methods)
        {
            var definition = new MethodDefinition(method.Name,
                AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes.Public | AsmResolver.PE.DotNet.Metadata.Tables.MethodAttributes.Static,
                MethodSignature.CreateStatic(module.CorLibTypeFactory.Void));
            managedType.Methods.Add(definition);
            output.Fill(definition, method);
        }

        var report = output.Snapshot(app);
        var selected = report.Methods.Where(m => m.AssemblyName == "RecoveryFixture").ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(selected, Has.Length.EqualTo(methods.Length));
            Assert.That(selected.Select(m => m.Disposition), Is.EqualTo(new[]
            {
                MethodRecoveryDisposition.NoNativeBody, MethodRecoveryDisposition.EmptyAnalysis,
                MethodRecoveryDisposition.SkippedMethodSize, MethodRecoveryDisposition.Failed,
                MethodRecoveryDisposition.Emitted, MethodRecoveryDisposition.Partial,
            }));
            Assert.That(selected.Select(m => m.Identity).Distinct().Count(), Is.EqualTo(methods.Length));
            Assert.That(selected.Last().Reasons, Does.Contain("Synthetic unresolved analysis warning."));
            Assert.That(methods.All(m => m.ConvertedIsil == null && m.ControlFlowGraph == null), Is.True);
        });
        Assert.Throws<IncompleteRecoveryException>(() => report.EnsureComplete(["RecoveryFixture"]));
    }

    [TestCase("System.dll", true)]
    [TestCase("UnityEngine.CoreModule", true)]
    [TestCase("Sample.System.Utility", false)]
    public void ReferenceAssemblyScopeIsExplicit(string assemblyName, bool excluded)
        => Assert.That(AsmResolverDllOutputFormatIlRecovery.IsReferenceAssembly(assemblyName), Is.EqualTo(excluded));

    private sealed class InspectableRecoveryOutput : AsmResolverDllOutputFormatIlRecovery
    {
        public void Begin(ApplicationAnalysisContext context) => BeginRecoveryReport(context);
        public void Fill(MethodDefinition definition, MethodAnalysisContext method) => FillMethodBody(definition, method);
        public RecoveryReport Snapshot(ApplicationAnalysisContext context) => CreateRecoveryReport(context, true);
    }

    private sealed class SyntheticNativeMethod : MethodAnalysisContext
    {
        public override string DefaultName { get; }
        public override ulong UnderlyingPointer { get; }
        public override TypeAnalysisContext DefaultReturnType => AppContext.SystemTypes.SystemVoidType;
        public override ReflectionMethodAttributes DefaultAttributes => ReflectionMethodAttributes.Public | ReflectionMethodAttributes.Static;
        public override ReflectionMethodImplAttributes DefaultImplAttributes => ReflectionMethodImplAttributes.Managed;

        public SyntheticNativeMethod(TypeAnalysisContext parent, string name, ulong address, List<Instruction> instructions) : base(null, parent)
        {
            DefaultName = name;
            UnderlyingPointer = address;
            ConvertedIsil = instructions;
            ControlFlowGraph = new ISILControlFlowGraph(instructions);
            RawBytes = new BinarySlice([0xC3]);
        }
    }
}
