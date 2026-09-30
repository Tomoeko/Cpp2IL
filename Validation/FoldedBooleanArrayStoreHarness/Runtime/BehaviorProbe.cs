using System;
using System.Collections.Generic;
using System.IO;
using FoldedBooleanArrayStoreFixture;
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
                var indices = new[] { int.MinValue, -1, 0, 1, length - 1, length, int.MaxValue };
                foreach (var index in indices)
                {
                    Record(observations, scenario, "first", index);
                    Record(observations, scenario, "second", index);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "folded-boolean-array-store" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, Scenario scenario, string kind, int index)
        {
            var first = kind == "first" && !scenario.MissingOwner
                ? new FirstArrayOwner
                {
                    Prefix0 = -41, Prefix1 = 73, Values = Copy(scenario.Initial), Suffix = 307
                }
                : null;
            var second = kind == "second" && !scenario.MissingOwner
                ? new SecondArrayOwner
                {
                    Prefix0 = -41, Prefix1 = 73, Values = Copy(scenario.Initial), Suffix = 307
                }
                : null;
            var original = first == null ? second == null ? null : second.Values : first.Values;
            var firstAlias = first == null ? null : new FirstArrayOwner
            {
                Values = original, Suffix = -19
            };
            var secondAlias = second == null ? null : new SecondArrayOwner
            {
                Values = original, Suffix = -19
            };
            var before = Copy(original);
            var exception = "none";
            try
            {
                if (kind == "first") first.SetFalse(index);
                else second.SetFalse(index);
            }
            catch (Exception error) { exception = error.GetType().FullName; }

            var current = first == null ? second == null ? null : second.Values : first.Values;
            var aliasCurrent = firstAlias == null
                ? secondAlias == null ? null : secondAlias.Values : firstAlias.Values;
            observations.Add(new Dictionary<string, object>
            {
                { "owner", kind }, { "case", scenario.Label }, { "index", index },
                { "exception", exception }, { "before", before },
                { "after", Copy(current) }, { "aliasAfter", Copy(aliasCurrent) },
                { "sameFieldReference", (first != null || second != null) &&
                    ReferenceEquals(current, original) },
                { "aliasSharesArray", original != null &&
                    ReferenceEquals(aliasCurrent, original) },
                { "prefix0", first == null ? second == null ? (long?)null : second.Prefix0 : first.Prefix0 },
                { "prefix1", first == null ? second == null ? (long?)null : second.Prefix1 : first.Prefix1 },
                { "suffix", first == null ? second == null ? (long?)null : second.Suffix : first.Suffix },
                { "aliasSuffix", firstAlias == null
                    ? secondAlias == null ? (long?)null : secondAlias.Suffix : firstAlias.Suffix }
            });
        }

        private static bool[] Copy(bool[] values) => values == null ? null : (bool[])values.Clone();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report") continue;
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
