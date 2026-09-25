using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RecoveryValidation
{
    public static class ValidationEntry
    {
        private const string RequiredVersion = "2021.3.35f1";

        private static void RequireVersion()
        {
            if (Application.unityVersion != RequiredVersion)
                throw new InvalidOperationException("Expected Unity " + RequiredVersion + ", received " + Application.unityVersion);
        }

        public static void Compile()
        {
            try
            {
                RecordCompilation();
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static void RecordCompilation()
        {
            RequireVersion();
            BehaviorProbe.Write("Reports/editor-behavior.json", "editor");
            File.WriteAllText("Reports/compilation-complete.txt", RequiredVersion + "\n");
        }

        public static void Build()
        {
            try
            {
                RecordCompilation();
                if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
                    throw new InvalidOperationException("Windows x64 target activation failed.");
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.IL2CPP);
                PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.Standalone, Il2CppCompilerConfiguration.Release);
                PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Standalone, ApiCompatibilityLevel.NET_4_6);
                PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, ManagedStrippingLevel.Low);
                PlayerSettings.companyName = "RecoveryValidation";
                PlayerSettings.productName = "RecoveryFixture";
                EditorUserBuildSettings.development = false;
                EditorUserBuildSettings.allowDebugging = false;
                EditorUserBuildSettings.connectProfiler = false;
                var codeGeneration = Environment.GetEnvironmentVariable("CPP2IL_VALIDATION_CODE_GENERATION");
                if (string.IsNullOrEmpty(codeGeneration) || codeGeneration == "OptimizeSpeed")
                    EditorUserBuildSettings.il2CppCodeGeneration = UnityEditor.Build.Il2CppCodeGeneration.OptimizeSpeed;
                else if (codeGeneration == "OptimizeSize")
                    EditorUserBuildSettings.il2CppCodeGeneration = UnityEditor.Build.Il2CppCodeGeneration.OptimizeSize;
                else
                    throw new InvalidOperationException("Unsupported IL2CPP code generation setting: " + codeGeneration);
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                if (!EditorSceneManager.SaveScene(scene, "Assets/Validation.unity"))
                    throw new InvalidOperationException("Failed to save the validation scene.");
                Directory.CreateDirectory("Reports");
                BuildReport report;
                var toolchainRoot = Environment.GetEnvironmentVariable("CPP2IL_VALIDATION_TOOLCHAIN");
                var previousProgramFiles = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
                var previousTools = Environment.GetEnvironmentVariable("VS160COMNTOOLS");
                try
                {
                    if (!string.IsNullOrEmpty(toolchainRoot))
                    {
                        if (!Directory.Exists(Path.Combine(toolchainRoot, "VC", "Tools", "MSVC")) ||
                            !Directory.Exists(Path.Combine(toolchainRoot, "Windows Kits", "10")))
                            throw new InvalidOperationException("The supplied toolchain needs VC/Tools/MSVC and Windows Kits/10.");
                        // Wine can replace ProgramFiles(x86) at startup. Set discovery hints
                        // only in this Editor process, without modifying registry or prefix.
                        Environment.SetEnvironmentVariable("ProgramFiles(x86)", toolchainRoot);
                        Environment.SetEnvironmentVariable("VS160COMNTOOLS", Path.Combine(toolchainRoot, "Common7", "Tools"));
                    }
                    report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                    {
                        scenes = new[] { "Assets/Validation.unity" },
                        target = BuildTarget.StandaloneWindows64,
                        locationPathName = "../player/RecoveryFixture.exe",
                        options = BuildOptions.None
                    });
                }
                finally
                {
                    if (!string.IsNullOrEmpty(toolchainRoot))
                    {
                        Environment.SetEnvironmentVariable("ProgramFiles(x86)", previousProgramFiles);
                        Environment.SetEnvironmentVariable("VS160COMNTOOLS", previousTools);
                    }
                }
                var receipt = new Dictionary<string, object>
                {
                    { "unityVersion", Application.unityVersion },
                    { "host", Application.platform.ToString() },
                    { "target", report.summary.platform.ToString() },
                    { "backend", PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone).ToString() },
                    { "compilerConfiguration", PlayerSettings.GetIl2CppCompilerConfiguration(BuildTargetGroup.Standalone).ToString() },
                    { "apiCompatibility", PlayerSettings.GetApiCompatibilityLevel(BuildTargetGroup.Standalone).ToString() },
                    { "stripping", PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.Standalone).ToString() },
                    { "codeGeneration", EditorUserBuildSettings.il2CppCodeGeneration.ToString() },
                    { "stripEngineCode", PlayerSettings.stripEngineCode },
                    { "scriptingDefineSymbols", PlayerSettings.GetScriptingDefineSymbolsForGroup(BuildTargetGroup.Standalone) },
                    { "development", EditorUserBuildSettings.development },
                    { "result", report.summary.result.ToString() },
                    { "errors", report.summary.totalErrors },
                    { "warnings", report.summary.totalWarnings },
                    { "bytes", report.summary.totalSize }
                };
                File.WriteAllText("Reports/build.json", ReportJson.Encode(receipt));
                EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }
    }
}
