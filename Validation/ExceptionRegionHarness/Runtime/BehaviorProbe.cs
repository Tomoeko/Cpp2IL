using System;
using System.Collections.Generic;
using System.IO;
using ExceptionRegionFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var cases = new[]
            {
                new { Dividend = 100, Divisor = 5, Initial = 0 },
                new { Dividend = 0, Divisor = 0, Initial = 1 },
                new { Dividend = 1, Divisor = 0, Initial = -1 },
                new { Dividend = -40, Divisor = 2, Initial = 17 },
                new { Dividend = 40, Divisor = -3, Initial = int.MaxValue - 1 },
                new { Dividend = -17, Divisor = -5, Initial = int.MinValue },
                new { Dividend = 12, Divisor = 1, Initial = int.MaxValue }
            };
            foreach (var test in cases)
            {
                object result = null;
                var exception = "none";
                try { result = ExceptionMethods.CatchZero(test.Dividend, test.Divisor); }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "method", "catch" }, { "dividend", test.Dividend }, { "divisor", test.Divisor },
                    { "initial", test.Initial }, { "counter", test.Initial },
                    { "result", result }, { "exception", exception }
                });
                var count = test.Initial;
                result = null;
                exception = "none";
                try { result = ExceptionMethods.FinallyCount(test.Divisor, ref count); }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "method", "finally" }, { "dividend", test.Dividend }, { "divisor", test.Divisor },
                    { "initial", test.Initial }, { "counter", count },
                    { "result", result }, { "exception", exception }
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "exception-regions" }, { "observations", observations }
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
