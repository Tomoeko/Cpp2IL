using System;
using System.Collections.Generic;
using System.IO;
using FieldArrayFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // This driver is independent of the selected field-array methods.
    public static class BehaviorProbe
    {
        private sealed class Scenario
        {
            public readonly string Label;
            public readonly bool MissingOwner;
            public readonly int[] Initial;
            public readonly int Neighbor;
            public readonly int AliasNeighbor;

            public Scenario(string label, bool missingOwner, int[] initial, int neighbor, int aliasNeighbor)
            {
                Label = label;
                MissingOwner = missingOwner;
                Initial = initial;
                Neighbor = neighbor;
                AliasNeighbor = aliasNeighbor;
            }
        }

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var scenarios = new[]
            {
                new Scenario("null-owner", true, null, 0, 0),
                new Scenario("null-array", false, null, 17, -17),
                new Scenario("empty", false, new int[0], -31, 31),
                new Scenario("single", false, new[] { int.MinValue }, 101, -101),
                new Scenario("mixed", false, new[] { -7, 0, int.MaxValue }, -303, 303)
            };

            foreach (var scenario in scenarios)
            {
                Record(observations, scenario, "read-first", null, null, owner => owner.ReadFirst());
                var length = scenario.Initial == null ? 0 : scenario.Initial.Length;
                var indices = new[] { int.MinValue, -1, 0, 1, length - 1, length, int.MaxValue };
                foreach (var index in indices)
                    Record(observations, scenario, "read-at", index, null, owner => owner.ReadAt(index));
                foreach (var index in indices)
                    foreach (var value in new[] { int.MinValue, int.MaxValue })
                        Record(observations, scenario, "write-at", index, value, owner =>
                        {
                            owner.WriteAt(index, value);
                            return null;
                        });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "field-array" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, Scenario scenario, string kind,
            object index, object value, Func<FieldArrayState, object> operation)
        {
            var owner = scenario.MissingOwner ? null : new FieldArrayState
            {
                Values = Copy(scenario.Initial), Neighbor = scenario.Neighbor
            };
            var originalArray = owner == null ? null : owner.Values;
            var alias = owner == null ? null : new FieldArrayState
            {
                Values = originalArray, Neighbor = scenario.AliasNeighbor
            };
            var before = Copy(originalArray);
            var neighborBefore = owner == null ? (object)null : owner.Neighbor;
            var aliasNeighborBefore = alias == null ? (object)null : alias.Neighbor;

            object result = null;
            var exception = "none";
            try { result = operation(owner); }
            catch (Exception error) { exception = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "case", scenario.Label }, { "index", index }, { "value", value },
                { "result", result }, { "exception", exception },
                { "before", before }, { "after", Copy(owner == null ? null : owner.Values) },
                { "aliasAfter", Copy(alias == null ? null : alias.Values) },
                { "sameFieldReference", owner != null && ReferenceEquals(owner.Values, originalArray) },
                { "aliasSharesArray", originalArray != null && alias != null &&
                    ReferenceEquals(alias.Values, originalArray) },
                { "neighborBefore", neighborBefore },
                { "neighborAfter", owner == null ? (object)null : owner.Neighbor },
                { "aliasNeighborBefore", aliasNeighborBefore },
                { "aliasNeighborAfter", alias == null ? (object)null : alias.Neighbor }
            });
        }

        private static int[] Copy(int[] values) => values == null ? null : (int[])values.Clone();

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
