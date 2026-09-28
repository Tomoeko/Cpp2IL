using System;
using System.Collections.Generic;
using System.IO;
using GuardedBoxedCastFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            object boxed = 17;
            var observations = new List<object>
            {
                Row("equal", boxed, 17),
                Row("different", boxed, 18),
                Row("repeat", boxed, 17),
                Row("other-value-type", 17L, 17),
                Row("other-reference-type", "17", 17),
                Row("null", null, 17)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "guarded-boxed-cast" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Row(string kind, object value, int expected)
        {
            var result = false;
            var exception = "none";
            try { result = GuardedCast.Matches(value, expected); }
            catch (Exception error) { exception = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", kind },
                { "result", result },
                { "exception", exception },
                { "valueIsNull", value == null }
            };
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
