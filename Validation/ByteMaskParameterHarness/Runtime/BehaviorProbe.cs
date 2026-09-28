using System;
using System.Collections.Generic;
using System.IO;
using ByteMaskParameterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var value = 0; value <= byte.MaxValue; value++)
            {
                var bits = (FlagBits)value;
                observations.Add(new Dictionary<string, object>
                {
                    { "bits", value },
                    { "two", ByteMaskProbe.HasTwo(0, bits) },
                    { "four", ByteMaskProbe.HasFour(int.MaxValue, bits) },
                    { "eight", ByteMaskProbe.HasEight(int.MinValue, bits) },
                    { "sixteen", ByteMaskProbe.HasSixteen(255, bits) },
                    { "thirtyTwo", ByteMaskProbe.HasThirtyTwo(-256, bits) },
                    { "sixtyFour", ByteMaskProbe.HasSixtyFour(value, bits) }
                });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "byte-mask-parameter" },
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
