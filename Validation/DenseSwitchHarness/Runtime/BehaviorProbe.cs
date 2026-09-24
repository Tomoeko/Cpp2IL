using System;
using System.Collections.Generic;
using System.IO;
using DenseSwitchFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] DenseSelectors =
        {
            int.MinValue, -2, -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12,
            13, 14, 15, 16, 17, int.MaxValue
        };

        private static readonly int[] SparseSelectors =
        {
            int.MinValue, -100001, -100000, -17, -1, 0, 1, 29, 30,
            99999, 100000, 100001, int.MaxValue
        };

        private static readonly int[] Values = { int.MinValue, -17, 0, 19, int.MaxValue };
        private static readonly int[] InitialTraces = { int.MinValue, 0, int.MaxValue };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            Record(observations, "dense", DenseSelectors);
            Record(observations, "sparse", SparseSelectors);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "dense-switch" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string method, int[] selectors)
        {
            foreach (var selector in selectors)
            {
                foreach (var value in Values)
                {
                    foreach (var initial in InitialTraces)
                    {
                        var trace = initial;
                        object result = null;
                        var exception = "none";
                        try
                        {
                            result = method == "dense"
                                ? SwitchMethods.Dense(selector, value, ref trace)
                                : SwitchMethods.Sparse(selector, value, ref trace);
                        }
                        catch (Exception error)
                        {
                            exception = error.GetType().FullName;
                        }

                        observations.Add(new Dictionary<string, object>
                        {
                            { "method", method },
                            { "selector", selector },
                            { "value", value },
                            { "initialTrace", initial },
                            { "trace", trace },
                            { "result", result },
                            { "exception", exception }
                        });
                    }
                }
            }
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
