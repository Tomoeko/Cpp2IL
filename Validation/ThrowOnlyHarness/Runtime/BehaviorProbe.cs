using System;
using System.Collections.Generic;
using System.IO;
using ThrowOnlyFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var first = new ThrowOnlyCases { Current = 5, Neighbor = 23 };
            var second = new ThrowOnlyCases { Current = 11, Neighbor = -29 };

            RecordThrow(observations, "unsupported:first", first, first, second, true);
            RecordThrow(observations, "unimplemented:first", first, first, second, false);
            RecordThrow(observations, "unsupported:second", second, first, second, true);
            RecordThrow(observations, "unsupported:null", null, first, second, true);
            RecordThrow(observations, "unimplemented:null", null, first, second, false);

            RecordReturn(observations, "return:first-negative", first, first, second, -2);
            RecordReturn(observations, "return:first-overflow", first, first, second, int.MaxValue);
            RecordReturn(observations, "return:second-underflow", second, first, second, int.MinValue);
            RecordReturn(observations, "return:null", null, first, second, 0);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "throw-only" },
                { "observations", observations }
            }));
        }

        private static void RecordThrow(List<object> observations, string kind, ThrowOnlyCases owner,
            ThrowOnlyCases first, ThrowOnlyCases second, bool notSupported)
        {
            string exception = "none";
            try
            {
                if (notSupported)
                    owner.ThrowNotSupported();
                else
                    owner.ThrowNotImplemented();
            }
            catch (Exception error)
            {
                exception = error.GetType().FullName;
            }
            Record(observations, kind, null, exception, first, second);
        }

        private static void RecordReturn(List<object> observations, string kind, ThrowOnlyCases owner,
            ThrowOnlyCases first, ThrowOnlyCases second, int input)
        {
            int? result = null;
            string exception = "none";
            try
            {
                result = owner.ReturnAfterCall(input);
            }
            catch (Exception error)
            {
                exception = error.GetType().FullName;
            }
            Record(observations, kind, result, exception, first, second);
        }

        private static void Record(List<object> observations, string kind, int? result, string exception,
            ThrowOnlyCases first, ThrowOnlyCases second)
        {
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "result", result },
                { "exception", exception },
                { "firstCurrent", first.Current },
                { "firstNeighbor", first.Neighbor },
                { "firstCalls", first.Calls },
                { "secondCurrent", second.Current },
                { "secondNeighbor", second.Neighbor },
                { "secondCalls", second.Calls }
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
