using System;
using System.Collections.Generic;
using System.IO;
using ReferenceNullFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var box = new ReferenceBox { Neighbor = 23 };
            Record(observations, "both-null", null, null);
            Record(observations, "empty-array", box, Array.Empty<int>());
            Record(observations, "nonempty-array", box, new[] { -17, 0, 17 });
            Record(observations, "null-array", box, null);
            Record(observations, "null-class", null, new[] { 17 });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "reference-null" }, { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, ReferenceBox box,
            int[] numbers)
        {
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "classIsNull", ReferenceNullCases.ClassIsNull(box) },
                { "arrayHasValue", ReferenceNullCases.ArrayHasValue(numbers) },
                { "arrayLength", numbers == null ? (int?)null : numbers.Length },
                { "neighbor", box == null ? (int?)null : box.Neighbor }
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
