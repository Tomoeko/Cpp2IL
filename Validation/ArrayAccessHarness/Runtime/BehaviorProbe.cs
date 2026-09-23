using System;
using System.Collections.Generic;
using System.IO;
using ArrayAccessFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var arrays = new[]
            {
                new { Label = "empty", Values = new int[0] },
                new { Label = "single", Values = new[] { int.MinValue } },
                new { Label = "mixed", Values = new[] { -7, 0, 19, int.MaxValue } }
            };
            foreach (var array in arrays)
                Record(observations, array.Label, array.Values);
            Record(observations, "null", null);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "array-access" }, { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string label, int[] values)
        {
            var length = values == null ? 0 : values.Length;
            foreach (var index in new[] { int.MinValue, -1, 0, 1, length - 1, length, int.MaxValue })
            {
                object result = null;
                var exception = "none";
                try { result = ArrayReads.Read(values, index); }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", label }, { "index", index }, { "result", result }, { "exception", exception }
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
