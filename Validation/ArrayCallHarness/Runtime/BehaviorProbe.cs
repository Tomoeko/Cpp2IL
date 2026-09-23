using System;
using System.Collections.Generic;
using System.IO;
using ArrayCallFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var echo = new ArrayEcho();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "created", echo != null }, { "callsAfter", echo.Calls }
            });
            Record(observations, "values", echo, new[] { int.MinValue, 0, int.MaxValue });
            Record(observations, "empty", echo, new int[0]);
            Record(observations, "null-array", echo, null);
            echo.Calls = int.MaxValue;
            Record(observations, "overflow", echo, new[] { int.MinValue, int.MaxValue });
            Record(observations, "null-receiver-values", null, new[] { -17, 19 });
            Record(observations, "null-receiver-null-array", null, null);
            var derived = new DerivedEcho();
            RecordDerived(observations, "derived-values", derived, new[] { int.MinValue, 0, int.MaxValue });
            RecordDerived(observations, "derived-null-receiver", null, new[] { -17, 19 });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "array-call" }, { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, ArrayEcho echo, int[] values)
        {
            object result = null;
            var sameReference = false;
            var exception = "none";
            try
            {
                var returned = ArrayCalls.Forward(echo, values);
                result = returned;
                sameReference = ReferenceEquals(returned, values);
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "result", result },
                { "sameReference", sameReference }, { "exception", exception },
                { "callsAfter", echo == null ? null : (object)echo.Calls }
            });
        }

        private static void RecordDerived(List<object> observations, string kind, DerivedEcho echo, int[] values)
        {
            object result = null;
            var sameReference = false;
            var exception = "none";
            try
            {
                var returned = ArrayCalls.ForwardDerived(echo, values);
                result = returned;
                sameReference = ReferenceEquals(returned, values);
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "result", result },
                { "sameReference", sameReference }, { "exception", exception },
                { "callsAfter", echo == null ? null : (object)echo.Calls }
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
