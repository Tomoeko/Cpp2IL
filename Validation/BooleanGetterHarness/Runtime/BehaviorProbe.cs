using System;
using System.Collections.Generic;
using System.IO;
using BooleanGetterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Neighbors = { 0, int.MinValue, int.MaxValue };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var neighbor in Neighbors)
            foreach (var value in new[] { false, true })
            {
                var first = new FirstState { Value = value, Neighbor = neighbor };
                var second = new SecondState { Value = !value, Neighbor = neighbor };
                observations.Add(Row("first", value, neighbor, first.ReadValue(),
                    first.Value, first.Neighbor));
                observations.Add(Row("second", !value, neighbor, second.ReadValue(),
                    second.Value, second.Neighbor));
            }

            FirstState missingFirst = null;
            SecondState missingSecond = null;
            RecordNull(observations, "first", () => missingFirst.ReadValue());
            RecordNull(observations, "second", () => missingSecond.ReadValue());

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "boolean-getter" },
                { "observations", observations }
            }));
        }

        private static object Row(string owner, bool before, int neighborBefore,
            bool result, bool after, int neighborAfter) => new Dictionary<string, object>
        {
            { "kind", "read" }, { "owner", owner },
            { "valueBefore", before }, { "neighborBefore", neighborBefore },
            { "result", result }, { "valueAfter", after },
            { "neighborAfter", neighborAfter }
        };

        private static void RecordNull(List<object> observations, string owner, Action action)
        {
            var exception = "none";
            try { action(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "null" }, { "owner", owner }, { "exception", exception }
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
