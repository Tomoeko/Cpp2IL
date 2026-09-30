using System;
using System.Collections.Generic;
using System.IO;
using ConditionalManagedThrowFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            Record(observations, "zero", false, 0);
            Record(observations, "negative", false, -2);
            Record(observations, "positive-edge", false, int.MaxValue);
            Record(observations, "throw-zero", true, 0);
            Record(observations, "throw-negative-edge", true, int.MinValue);
            Record(observations, "negative-edge", false, int.MinValue);
            Record(observations, "throw-positive-edge", true, int.MaxValue);
            Record(observations, "after-throw", false, 37);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "conditional-managed-throw" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, bool fail, int value)
        {
            int? result = null;
            string exception = "none";
            try
            {
                result = ConditionalManagedThrow.ReturnOrThrow(fail, value);
            }
            catch (Exception error)
            {
                exception = error.GetType().FullName;
            }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "fail", fail },
                { "value", value },
                { "result", result },
                { "exception", exception }
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
