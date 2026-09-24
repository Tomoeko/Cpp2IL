using System;
using System.Collections.Generic;
using System.IO;
using StaticLiteralConcatFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var repeatedInput = new string('v', 2);
            Observe(observations, "null", null, 43);
            Observe(observations, "empty", string.Empty, -17);
            Observe(observations, "first", repeatedInput, 0);
            Observe(observations, "repeat", repeatedInput, 101);
            Observe(observations, "unicode", "\u03A9\u0301\U0001F642", -59);
            Observe(observations, "suffix-inside", "x|tag", int.MaxValue);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "static-literal-concat" },
                { "observations", observations }
            }));
        }

        private static void Observe(List<object> observations, string label,
            string value, int neighbor)
        {
            LiteralJoiner.Neighbor = neighbor;
            var inputBefore = value;
            var neighborBefore = LiteralJoiner.Neighbor;
            string result = null;
            var exception = "none";
            try { result = LiteralJoiner.AppendLiteral(value); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", label }, { "inputAfter", value },
                { "sameInputAfter", ReferenceEquals(inputBefore, value) },
                { "result", result },
                { "sameResultAsInput", ReferenceEquals(result, inputBefore) },
                { "exception", exception },
                { "neighborBefore", neighborBefore },
                { "neighborAfter", LiteralJoiner.Neighbor }
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
