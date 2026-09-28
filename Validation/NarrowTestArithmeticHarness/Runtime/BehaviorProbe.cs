using System;
using System.Collections.Generic;
using System.IO;
using NarrowTestArithmeticFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Values =
        {
            int.MinValue, int.MinValue + 16, -18, -1, 0, 1, 18,
            int.MaxValue - 16, int.MaxValue
        };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var value in Values)
            foreach (var add in new[] { false, true })
            {
                observations.Add(new Dictionary<string, object>
                {
                    { "value", value },
                    { "add", add },
                    { "selected", BranchArithmetic.Select(value, add) }
                });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "narrow-test-arithmetic" },
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
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    Application.Quit(1);
                }
                return;
            }
        }
    }
}
