using System;
using System.Collections.Generic;
using System.IO;
using BooleanParameterBranchFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var value in new[] { int.MinValue, -17, -1, 0, 1, 17, int.MaxValue })
            {
                foreach (var useHighPath in new[] { false, true })
                {
                    observations.Add(new Dictionary<string, object>
                    {
                        { "value", value },
                        { "useHighPath", useHighPath },
                        { "selected", BranchMethods.Select(value, useHighPath) },
                        { "high", BranchMethods.HighPath() },
                        { "low", BranchMethods.LowPath() }
                    });
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "boolean-parameter-branch" },
                { "observations", observations }
            }));
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report")
                    continue;
                try
                {
                    Write(arguments[index + 1], "player");
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
