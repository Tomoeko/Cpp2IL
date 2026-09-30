using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var directory = Path.Combine(Path.GetDirectoryName(path), "batch");
            WriteReports(directory, stage);
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "stage", stage },
                { "profiles", BatchProfiles.Names }
            }));
        }

        private static void WriteReports(string directory, string stage)
        {
            Directory.CreateDirectory(directory);
            for (var index = 0; index < BatchProfiles.Names.Length; index++)
            {
                var profile = BatchProfiles.Names[index];
                var assembly = Assembly.Load(BatchProfiles.Assemblies[index]);
                var probe = assembly.GetType("RecoveryValidation.BehaviorProbe", true);
                var write = probe.GetMethod("Write", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(string), typeof(string) }, null);
                if (write == null || write.ReturnType != typeof(void))
                    throw new InvalidOperationException("The batch harness has no public report entry point: " + profile);
                var path = stage == "editor"
                    ? Path.Combine(directory, profile, "editor-behavior.json")
                    : Path.Combine(directory, profile + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                write.Invoke(null, new object[] { path, stage });
                if (!File.Exists(path))
                    throw new InvalidOperationException("The batch harness did not produce its report: " + profile);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-batch-report-directory")
                    continue;
                try
                {
                    WriteReports(arguments[index + 1], "player");
                    Application.Quit(0);
                }
                catch (Exception error)
                {
                    Debug.LogException(error);
                    Application.Quit(1);
                }
                return;
            }
        }
    }
}
