using System;
using System.Collections.Generic;
using System.IO;
using ParameterArrayFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // The independent driver stays outside the selected recovery assembly.
    public static class BehaviorProbe
    {
        private sealed class Scenario
        {
            public readonly string Label;
            public readonly int[] Left;
            public readonly int[] Right;
            public readonly bool ShareArray;
            public readonly int Marker;

            public Scenario(string label, int[] left, int[] right,
                bool shareArray = false, int marker = 47)
            {
                Label = label;
                Left = left;
                Right = right;
                ShareArray = shareArray;
                Marker = marker;
            }
        }

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var scenarios = new[]
            {
                new Scenario("left-null", null, new[] { 0, 1 }),
                new Scenario("right-null", new[] { 0, 1 }, null),
                new Scenario("both-null", null, null),
                new Scenario("left-empty", new int[0], new[] { 0, 1 }),
                new Scenario("right-empty", new[] { 0, 1 }, new int[0]),
                new Scenario("both-empty", new int[0], new int[0]),
                new Scenario("equal-distinct", new[] { 0, 1 }, new[] { 0, 1 }),
                new Scenario("unequal-distinct", new[] { 0, 1 }, new[] { 2, 0 }),
                new Scenario("same-array", new[] { 0, 1, -1 }, null,
                    shareArray: true),
                new Scenario("null-elements", new[] { -1, 0 }, new[] { -1, 1 }),
                new Scenario("one-null", new[] { -1, 0 }, new[] { 1, -1 }),
                new Scenario("left-longer", new[] { 0, 1 }, new[] { 0 }),
                new Scenario("right-longer", new[] { 0 }, new[] { 0, 1 }),
                new Scenario("marker-wrap", new[] { 0, 1 }, new[] { 0, 2 },
                    marker: int.MaxValue)
            };

            foreach (var scenario in scenarios)
            {
                var leftLength = scenario.Left == null ? 0 : scenario.Left.Length;
                var rightLength = scenario.ShareArray ? leftLength :
                    scenario.Right == null ? 0 : scenario.Right.Length;
                Record(observations, scenario, "zero", 0);
                Record(observations, scenario, "one", 1);
                Record(observations, scenario, "last-left", leftLength - 1);
                Record(observations, scenario, "last-right", rightLength - 1);
                Record(observations, scenario, "upper-both",
                    Math.Max(leftLength, rightLength));
                Record(observations, scenario, "negative", -1);
                Record(observations, scenario, "minimum", int.MinValue);
                Record(observations, scenario, "maximum", int.MaxValue);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "parameter-array" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, Scenario scenario,
            string kind, int index)
        {
            var pool = new[] { new object(), new object(), new object() };
            var left = Materialize(scenario.Left, pool);
            var right = scenario.ShareArray ? left : Materialize(scenario.Right, pool);
            var reader = new ArrayReader { Marker = scenario.Marker };
            var leftBefore = Ids(left, pool);
            var rightBefore = Ids(right, pool);

            object result = null;
            var exception = "none";
            try
            {
                result = reader.CompareWithMark(left, right, index);
            }
            catch (Exception error)
            {
                exception = error.GetType().FullName;
            }

            observations.Add(new Dictionary<string, object>
            {
                { "case", scenario.Label }, { "kind", kind }, { "index", index },
                { "result", result }, { "exception", exception },
                { "leftBefore", leftBefore }, { "leftAfter", Ids(left, pool) },
                { "rightBefore", rightBefore }, { "rightAfter", Ids(right, pool) },
                { "markerBefore", scenario.Marker }, { "markerAfter", reader.Marker },
                { "sameArrays", left != null && ReferenceEquals(left, right) }
            });
        }

        private static object[] Materialize(int[] ids, object[] pool)
        {
            if (ids == null)
                return null;
            var values = new object[ids.Length];
            for (var index = 0; index < ids.Length; index++)
                values[index] = ids[index] < 0 ? null : pool[ids[index]];
            return values;
        }

        private static int[] Ids(object[] values, object[] pool)
        {
            if (values == null)
                return null;
            var ids = new int[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                ids[index] = -2;
                if (values[index] == null)
                    ids[index] = -1;
                else
                    for (var candidate = 0; candidate < pool.Length; candidate++)
                        if (ReferenceEquals(values[index], pool[candidate]))
                        {
                            ids[index] = candidate;
                            break;
                        }
            }
            return ids;
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
