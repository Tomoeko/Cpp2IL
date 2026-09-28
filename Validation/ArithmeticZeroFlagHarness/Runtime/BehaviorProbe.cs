using System;
using System.Collections.Generic;
using System.IO;
using ArithmeticZeroFlagFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[,] IntPairs =
        {
            { 0, 0 }, { 1, 1 }, { -1, -1 },
            { int.MaxValue, -1 }, { int.MinValue, 1 },
            { int.MaxValue, 1 }, { -5, 2 }, { 5, -2 }, { 1, 0 }
        };

        private static readonly long[,] LongPairs =
        {
            { 0, 0 }, { 1, 1 }, { -1, -1 },
            { long.MaxValue, -1 }, { long.MinValue, 1 },
            { long.MaxValue, 1 }, { -5, 2 }, { 5, -2 }, { 1, 0 }
        };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var index = 0; index < IntPairs.GetLength(0); index++)
            {
                var left = IntPairs[index, 0];
                var right = IntPairs[index, 1];
                var subtractor = new ArithmeticZeroFlag { Last32 = 145 };
                observations.Add(Case("subtract32", left, right,
                    subtractor.SubtractAndTest(left, right), subtractor.Last32));
                var adder = new ArithmeticZeroFlag { Last32 = 145 };
                observations.Add(Case("add32", left, right,
                    adder.AddAndTest(left, right), adder.Last32));
            }

            for (var index = 0; index < LongPairs.GetLength(0); index++)
            {
                var left = LongPairs[index, 0];
                var right = LongPairs[index, 1];
                var probe = new ArithmeticZeroFlag { Last64 = -449 };
                observations.Add(Case("subtract64", left, right,
                    probe.SubtractWideAndTest(left, right), probe.Last64));
            }

            foreach (var method in new[] { "subtract32", "add32", "subtract64" })
            {
                ArithmeticZeroFlag missing = null;
                try
                {
                    if (method == "subtract32")
                        missing.SubtractAndTest(1, 1);
                    else if (method == "add32")
                        missing.AddAndTest(1, -1);
                    else
                        missing.SubtractWideAndTest(1, 1);
                    throw new InvalidOperationException("Null receiver was accepted.");
                }
                catch (NullReferenceException)
                {
                    observations.Add(new Dictionary<string, object>
                    {
                        { "method", method }, { "left", 1 },
                        { "right", method == "add32" ? -1 : 1 },
                        { "result", null }, { "stored", null },
                        { "exception", "System.NullReferenceException" }
                    });
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "arithmetic-zero-flag" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Case(string method, long left,
            long right, bool result, long stored)
        {
            return new Dictionary<string, object>
            {
                { "method", method }, { "left", left }, { "right", right },
                { "result", result }, { "stored", stored },
                { "exception", "none" }
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
