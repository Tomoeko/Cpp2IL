using System;
using System.Collections.Generic;
using System.IO;
using MetadataGuardParameterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            SharedState.Neighbor = 7;
            Record(observations, "initial", 0, 4);
            Record(observations, "positive", 17, -2);
            Record(observations, "negative", -17, 3);
            Record(observations, "positive-overflow", int.MaxValue, 1);
            Record(observations, "negative-overflow", int.MinValue, -1);
            Record(observations, "zero", 0, 0);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "metadata-guard-parameter" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, int shared, int parameter)
        {
            SharedState.Value = shared;
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "shared", SharedState.Value },
                { "neighbor", SharedState.Neighbor },
                { "parameter", parameter },
                { "result", SharedState.AddParameter(parameter) }
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
