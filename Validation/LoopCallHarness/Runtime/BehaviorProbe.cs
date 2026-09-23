using System;
using System.Collections.Generic;
using System.IO;
using LoopCallFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var fresh = new LoopState();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "valueAfter", fresh.Value }, { "callsAfter", fresh.Calls }
            });
            var values = new[] { int.MinValue, -2, 0, 3, int.MaxValue };
            var callCounts = new[] { -1, 0, int.MaxValue };
            var counts = new[] { -3, 0, 1, 2, 7, 16 };
            var deltas = new[] { int.MinValue, -1, 0, 1, int.MaxValue };
            var seeds = new[] { int.MinValue, -7, 0, 19, int.MaxValue };

            foreach (var initialValue in values)
            foreach (var initialCalls in callCounts)
            foreach (var delta in deltas)
            {
                var state = new LoopState { Value = initialValue, Calls = initialCalls };
                var result = state.Step(delta);
                observations.Add(Row("step", initialValue, initialCalls, "delta", delta, result, state));
            }

            foreach (var initialValue in values)
            foreach (var initialCalls in callCounts)
            foreach (var count in counts)
            foreach (var seed in seeds)
            {
                var state = new LoopState { Value = initialValue, Calls = initialCalls };
                var result = LoopState.Run(state, count, seed);
                var row = Row("run", initialValue, initialCalls, "count", count, result, state);
                row.Add("seed", seed);
                observations.Add(row);
            }

            foreach (var initialValue in values)
            foreach (var initialCalls in callCounts)
            foreach (var count in counts)
            foreach (var stop in new[] { unchecked(initialValue - 1), initialValue, unchecked(initialValue + 1), unchecked(initialValue + 6), 0 })
            {
                var state = new LoopState { Value = initialValue, Calls = initialCalls };
                var result = LoopState.RunUntil(state, count, stop);
                var row = Row("until", initialValue, initialCalls, "count", count, result, state);
                row.Add("stop", stop);
                observations.Add(row);
            }

            LoopState missing = null;
            RecordNull(observations, "step", 1, () => missing.Step(1));
            foreach (var count in new[] { -3, 0, 1 })
            {
                RecordNull(observations, "run", count, () => LoopState.Run(missing, count, 19));
                RecordNull(observations, "until", count, () => LoopState.RunUntil(missing, count, 0));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "loop-calls" }, { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Row(string kind, int initialValue, int initialCalls,
            string argumentName, int argument, int result, LoopState state)
        {
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "initialValue", initialValue }, { "initialCalls", initialCalls },
                { argumentName, argument }, { "result", result }, { "valueAfter", state.Value }, { "callsAfter", state.Calls }
            };
        }

        private static void RecordNull(List<object> observations, string member, int argument, Func<int> action)
        {
            var exceptionType = "none";
            object result = null;
            try { result = action(); }
            catch (Exception exception) { exceptionType = exception.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "null" }, { "member", member }, { "argument", argument },
                { "result", result }, { "exception", exceptionType }
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
