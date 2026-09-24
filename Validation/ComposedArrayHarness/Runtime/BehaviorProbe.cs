using System;
using System.Collections.Generic;
using System.IO;
using ComposedArrayFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // This driver stays outside the assembly selected for recovery.
    public static class BehaviorProbe
    {
        private sealed class Scenario
        {
            public readonly string Label;
            public readonly bool MissingOwner;
            public readonly bool MissingHolder;
            public readonly int[] Initial;
            public readonly int[] Replacement;
            public readonly bool ShareReplacement;
            public readonly int Counter;
            public readonly int Marker;

            public Scenario(string label, int[] initial, int[] replacement,
                bool shareReplacement = false, bool missingOwner = false,
                bool missingHolder = false, int counter = 23, int marker = 47)
            {
                Label = label;
                Initial = initial;
                Replacement = replacement;
                ShareReplacement = shareReplacement;
                MissingOwner = missingOwner;
                MissingHolder = missingHolder;
                Counter = counter;
                Marker = marker;
            }
        }

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var scenarios = new[]
            {
                new Scenario("null-owner", null, null, missingOwner: true),
                new Scenario("null-holder", null, null, missingHolder: true),
                new Scenario("null-array", null, new[] { 0 }),
                new Scenario("empty", new int[0], new[] { 0 }),
                new Scenario("single-shared", new[] { 0 }, null, shareReplacement: true),
                new Scenario("single-distinct", new[] { 0 }, new[] { 1 }),
                new Scenario("pair-swapped", new[] { 0, 1 }, new[] { 1, 0 }),
                new Scenario("repeated-elements", new[] { 0, 0, 2 }, new[] { 2, 0, 0 }),
                new Scenario("null-elements", new[] { -1, 0, -1 }, new[] { 0, -1, -1 }),
                new Scenario("replacement-null", new[] { 0 }, null),
                new Scenario("replacement-shorter", new[] { 0, 1, 2 }, new[] { 2 }),
                new Scenario("counter-marker-wrap", new[] { 0 }, new[] { 1 },
                    counter: int.MaxValue, marker: int.MaxValue)
            };

            foreach (var scenario in scenarios)
            {
                var initialLength = scenario.Initial == null ? 0 : scenario.Initial.Length;
                var replacementLength = scenario.ShareReplacement ? initialLength :
                    scenario.Replacement == null ? 0 : scenario.Replacement.Length;
                foreach (var method in new[] { "twice", "replacement" })
                {
                    var secondLength = method == "twice" ? initialLength : replacementLength;
                    Record(observations, scenario, method, "same-index", 0, 0);
                    Record(observations, scenario, method, "first-to-second", 0, 1);
                    Record(observations, scenario, method, "last-to-first", initialLength - 1, 0);
                    Record(observations, scenario, method, "first-to-last", 0, secondLength - 1);
                    Record(observations, scenario, method, "negative-first", -1, 0);
                    Record(observations, scenario, method, "negative-second", 0, -1);
                    Record(observations, scenario, method, "upper-first", initialLength, 0);
                    Record(observations, scenario, method, "upper-second", 0, secondLength);
                    Record(observations, scenario, method, "both-negative", -1, -1);
                    Record(observations, scenario, method, "minimum-first",
                        int.MinValue, int.MaxValue);
                    Record(observations, scenario, method, "maximum-second", 0, int.MaxValue);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "composed-array" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, Scenario scenario,
            string method, string kind, int firstIndex, int secondIndex)
        {
            var pool = new[] { new object(), new object(), new object() };
            var holder = scenario.MissingOwner || scenario.MissingHolder ? null : new ArrayHolder
            {
                Items = Materialize(scenario.Initial, pool), Counter = scenario.Counter
            };
            var original = holder == null ? null : holder.Items;
            var replacement = holder == null ? null :
                (scenario.ShareReplacement ? original : Materialize(scenario.Replacement, pool));
            var reader = scenario.MissingOwner ? null : new ArrayReader
            {
                Holder = holder, Replacement = replacement, Marker = scenario.Marker
            };
            var originalBefore = Ids(original, pool);
            var replacementBefore = Ids(replacement, pool);

            object result = null;
            var exception = "none";
            try
            {
                result = method == "twice" ? reader.ReadTwice(firstIndex, secondIndex) :
                    reader.ReadAcrossReplacement(firstIndex, secondIndex);
            }
            catch (Exception error)
            {
                exception = error.GetType().FullName;
            }

            observations.Add(new Dictionary<string, object>
            {
                { "method", method }, { "kind", kind }, { "case", scenario.Label },
                { "firstIndex", firstIndex }, { "secondIndex", secondIndex },
                { "result", result }, { "exception", exception },
                { "originalBefore", originalBefore }, { "originalAfter", Ids(original, pool) },
                { "replacementBefore", replacementBefore },
                { "replacementAfter", Ids(replacement, pool) },
                { "counterBefore", holder == null ? (object)null : scenario.Counter },
                { "counterAfter", holder == null ? (object)null : holder.Counter },
                { "markerBefore", reader == null ? (object)null : scenario.Marker },
                { "markerAfter", reader == null ? (object)null : reader.Marker },
                { "sameFieldAsOriginal", holder != null &&
                    ReferenceEquals(holder.Items, original) },
                { "sameFieldAsReplacement", holder != null &&
                    ReferenceEquals(holder.Items, replacement) }
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
