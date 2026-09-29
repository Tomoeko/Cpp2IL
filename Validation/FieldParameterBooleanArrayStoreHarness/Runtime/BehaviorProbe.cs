using System;
using System.Collections.Generic;
using System.IO;
using FieldParameterBooleanArrayStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private sealed class Scenario
        {
            public readonly string Label;
            public readonly bool MissingOwner;
            public readonly bool[] Initial;

            public Scenario(string label, bool missingOwner, bool[] initial)
            {
                Label = label;
                MissingOwner = missingOwner;
                Initial = initial;
            }
        }

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var fresh = new BooleanArrayOwner();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "default-construction" },
                { "prefix0", fresh.Prefix0 }, { "prefix1", fresh.Prefix1 },
                { "prefix2", fresh.Prefix2 },
                { "valuesNull", fresh.Values == null },
                { "otherValuesNull", fresh.OtherValues == null },
                { "neighbor", fresh.Neighbor }
            });
            var scenarios = new[]
            {
                new Scenario("null-owner", true, null),
                new Scenario("null-array", false, null),
                new Scenario("empty", false, new bool[0]),
                new Scenario("single-false", false, new[] { false }),
                new Scenario("single-true", false, new[] { true }),
                new Scenario("mixed", false, new[] { true, false, true })
            };

            foreach (var scenario in scenarios)
            {
                var length = scenario.Initial == null ? 0 : scenario.Initial.Length;
                var indices = new[] { int.MinValue, -1, 0, 1, length - 1,
                    length, int.MaxValue };
                foreach (var index in indices)
                {
                    Record(observations, scenario, index, false);
                    Record(observations, scenario, index, true);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "field-parameter-boolean-array-store" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, Scenario scenario,
            int index, bool value)
        {
            var owner = scenario.MissingOwner ? null : new BooleanArrayOwner
            {
                Prefix0 = -41, Prefix1 = 73, Prefix2 = -109,
                Values = Copy(scenario.Initial),
                OtherValues = new[] { false, true, false }, Neighbor = 307
            };
            var original = owner == null ? null : owner.Values;
            var other = owner == null ? null : owner.OtherValues;
            var alias = owner == null ? null : new BooleanArrayOwner
            {
                Values = original, Neighbor = -19
            };
            var before = Copy(original);
            var failure = "none";
            try { owner.SetAt(index, value); }
            catch (Exception error) { failure = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "case", scenario.Label }, { "index", index }, { "value", value },
                { "failure", failure }, { "before", before },
                { "after", Copy(owner == null ? null : owner.Values) },
                { "aliasAfter", Copy(alias == null ? null : alias.Values) },
                { "sameFieldReference", owner != null && ReferenceEquals(owner.Values, original) },
                { "aliasSharesArray", original != null && alias != null &&
                    ReferenceEquals(alias.Values, original) },
                { "otherSame", owner == null ? (bool?)null : ReferenceEquals(other, owner.OtherValues) },
                { "otherAfter", Copy(owner == null ? null : owner.OtherValues) },
                { "prefix0", owner == null ? (long?)null : owner.Prefix0 },
                { "prefix1", owner == null ? (long?)null : owner.Prefix1 },
                { "prefix2", owner == null ? (long?)null : owner.Prefix2 },
                { "neighbor", owner == null ? (int?)null : owner.Neighbor },
                { "aliasNeighbor", alias == null ? (int?)null : alias.Neighbor }
            });
        }

        private static bool[] Copy(bool[] values) => values == null ? null : (bool[])values.Clone();

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
