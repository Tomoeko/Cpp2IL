using System;
using System.Collections.Generic;
using System.IO;
using ExplicitClassCastFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var exact = new Exception("exact");
            var subtype = new InvalidOperationException("subtype");
            var observations = new List<object>
            {
                Row("null", null, null),
                Row("exact", exact, exact),
                Row("subtype", subtype, subtype),
                Row("exact-repeat", exact, exact),
                Row("string", "other", null),
                Row("boxed-int", 17, null)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "explicit-class-cast" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Row(string kind, object value, Exception expected)
        {
            Exception result = null;
            var failure = "none";
            try { result = CastMethods.Cast(value); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", kind },
                { "sameReference", ReferenceEquals(result, expected) },
                { "resultType", result == null ? "null" : result.GetType().FullName },
                { "failure", failure }
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
