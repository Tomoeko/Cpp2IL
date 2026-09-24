using System;
using System.Collections.Generic;
using System.IO;
using ComposedReadFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // This driver stays outside the assembly selected for recovery.
    public static class BehaviorProbe
    {
        private sealed class Scenario
        {
            public readonly string Label;
            public readonly int[] Items;
            public readonly bool MissingOwner;
            public readonly bool MissingHolder;
            public readonly int Counter;
            public readonly int Marker;

            public Scenario(string label, int[] items, bool missingOwner = false,
                bool missingHolder = false, int counter = 23, int marker = 47)
            {
                Label = label;
                Items = items;
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
                new Scenario("null-owner", null, missingOwner: true),
                new Scenario("null-holder", null, missingHolder: true),
                new Scenario("null-array", null),
                new Scenario("empty", new int[0]),
                new Scenario("single", new[] { 0 }),
                new Scenario("distinct-elements", new[] { 0, 1 }),
                new Scenario("repeated-elements", new[] { 0, 0, 2 }),
                new Scenario("null-elements", new[] { -1, 0, -1 }),
                new Scenario("counter-marker-wrap", new[] { 0, 1 },
                    counter: int.MaxValue, marker: int.MaxValue)
            };

            foreach (var scenario in scenarios)
            {
                var length = scenario.Items == null ? 0 : scenario.Items.Length;
                Record(observations, scenario, "same-index", 0, 0);
                Record(observations, scenario, "first-to-second", 0, 1);
                Record(observations, scenario, "last-to-first", length - 1, 0);
                Record(observations, scenario, "first-to-last", 0, length - 1);
                Record(observations, scenario, "negative-first", -1, 0);
                Record(observations, scenario, "negative-second", 0, -1);
                Record(observations, scenario, "upper-first", length, 0);
                Record(observations, scenario, "upper-second", 0, length);
                Record(observations, scenario, "both-negative", -1, -1);
                Record(observations, scenario, "minimum-first", int.MinValue, int.MaxValue);
                Record(observations, scenario, "maximum-second", 0, int.MaxValue);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "composed-read" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, Scenario scenario,
            string kind, int firstIndex, int secondIndex)
        {
            var pool = new[] { new object(), new object(), new object() };
            var holder = scenario.MissingOwner || scenario.MissingHolder ? null : new ArrayHolder
            {
                Items = Materialize(scenario.Items, pool), Counter = scenario.Counter
            };
            var original = holder == null ? null : holder.Items;
            var reader = scenario.MissingOwner ? null : new ArrayReader
            {
                Holder = holder, Marker = scenario.Marker
            };
            var itemsBefore = Ids(original, pool);

            object result = null;
            var exception = "none";
            try
            {
                result = reader.ReadTwice(firstIndex, secondIndex);
            }
            catch (Exception error)
            {
                exception = error.GetType().FullName;
            }

            observations.Add(new Dictionary<string, object>
            {
                { "case", scenario.Label }, { "kind", kind },
                { "firstIndex", firstIndex }, { "secondIndex", secondIndex },
                { "result", result }, { "exception", exception },
                { "itemsBefore", itemsBefore }, { "itemsAfter", Ids(original, pool) },
                { "counterBefore", holder == null ? (object)null : scenario.Counter },
                { "counterAfter", holder == null ? (object)null : holder.Counter },
                { "markerBefore", reader == null ? (object)null : scenario.Marker },
                { "markerAfter", reader == null ? (object)null : reader.Marker },
                { "sameItemsAsOriginal", holder != null &&
                    ReferenceEquals(holder.Items, original) },
                { "sameHolderAsOriginal", reader != null &&
                    ReferenceEquals(reader.Holder, holder) }
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
