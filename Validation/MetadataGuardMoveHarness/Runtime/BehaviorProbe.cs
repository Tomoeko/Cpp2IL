using System;
using System.Collections.Generic;
using System.IO;
using MetadataGuardMoveFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var box = new ValueBox();
            SharedState.Neighbor = 7;
            Record(observations, "initial", box, 4);

            SharedState.Value = 17;
            box.Value = -5;
            Record(observations, "positive", box, -2);

            SharedState.Value = -17;
            box.Value = 23;
            Record(observations, "negative", box, 0);

            SharedState.Value = int.MaxValue;
            box.Value = 1;
            Record(observations, "positive-overflow", box, 1);

            SharedState.Value = int.MinValue;
            box.Value = -1;
            Record(observations, "negative-overflow", box, -1);

            SharedState.Value = 99;
            Record(observations, "null-box", null, 0);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "metadata-guard-move" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, ValueBox box, int parameter)
        {
            int? boxedResult = null;
            string error = null;
            try
            {
                boxedResult = SharedState.AddBox(box);
            }
            catch (Exception exception)
            {
                error = exception.GetType().FullName;
            }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "shared", SharedState.Value },
                { "neighbor", SharedState.Neighbor },
                { "parameter", parameter },
                { "boxValue", box == null ? (int?)null : box.Value },
                { "parameterResult", SharedState.AddParameter(parameter) },
                { "boxResult", boxedResult },
                { "boxError", error }
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
