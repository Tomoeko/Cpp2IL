using System;
using System.Collections.Generic;
using System.IO;
using StaticScalarSetterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            Record(observations, "initial");

            StaticState.Before = -1234567;
            StaticState.After = 7654321;
            Record(observations, "neighbors-set");

            foreach (var row in new[]
            {
                new { Phase = "first", Value = 17 },
                new { Phase = "repeat", Value = 17 },
                new { Phase = "negative", Value = -17 },
                new { Phase = "minimum", Value = int.MinValue },
                new { Phase = "maximum", Value = int.MaxValue },
                new { Phase = "zero", Value = 0 }
            })
            {
                StaticState.Assign(row.Value);
                Record(observations, row.Phase);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "static-scalar-setter" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string phase)
        {
            observations.Add(new Dictionary<string, object>
            {
                { "phase", phase },
                { "before", StaticState.Before },
                { "value", StaticState.Value },
                { "after", StaticState.After }
            });
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
