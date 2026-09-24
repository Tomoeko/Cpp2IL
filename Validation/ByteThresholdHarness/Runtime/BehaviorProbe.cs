using System;
using System.Collections.Generic;
using System.IO;
using ByteThresholdFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly byte[] Values = { 0, 1, 127, 128, 255 };
        private static readonly int[] Neighbors = { -17, 0, 17, int.MinValue, int.MaxValue };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var index = 0; index < Values.Length; index++)
            {
                var state = new ByteThresholdState
                {
                    Value = Values[index],
                    Neighbor = Neighbors[index]
                };
                var highBit = state.HasHighBit();
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "value" }, { "value", (int)Values[index] },
                    { "neighborBefore", Neighbors[index] }, { "highBit", highBit },
                    { "valueAfter", (int)state.Value }, { "neighborAfter", state.Neighbor }
                });
            }

            ByteThresholdState missing = null;
            var exceptionType = "none";
            try { missing.HasHighBit(); }
            catch (Exception exception) { exceptionType = exception.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "null" }, { "exception", exceptionType }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "byte-threshold" }, { "observations", observations }
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
