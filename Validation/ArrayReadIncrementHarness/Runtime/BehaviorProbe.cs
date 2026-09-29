using System;
using System.Collections.Generic;
using System.IO;
using ArrayReadIncrementFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            Record(observations, "empty", new int[0], new[] { -1, 0, int.MaxValue });
            Record(observations, "single-max", new[] { int.MaxValue },
                new[] { int.MinValue, -1, 0, 1, int.MaxValue });
            Record(observations, "mixed", new[] { -7, 0, 17, int.MaxValue },
                new[] { int.MinValue, -1, 0, 1, 2, 3, 4, int.MaxValue });
            Record(observations, "null", null,
                new[] { int.MinValue, -1, 0, 3, int.MaxValue });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "array-read-increment" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, int[] values, int[] indices)
        {
            foreach (var index in indices)
            {
                object result = null;
                var exception = "none";
                try { result = ArrayReadIncrement.ReadThenIncrement(values, index); }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", kind },
                    { "index", index },
                    { "result", result },
                    { "exception", exception },
                    { "valuesAfter", values == null ? null : (int[])values.Clone() }
                });
            }
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
