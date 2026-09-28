using System;
using System.Collections.Generic;
using System.IO;
using ConstructedBaseBooleanArrayFixture;
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
                    Record(observations, scenario, "set-true", index,
                        owner => owner.SetTrue(index));
                    Record(observations, scenario, "set-false", index,
                        owner => owner.SetFalse(index));
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "constructed-base-boolean-array" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, Scenario scenario, string kind,
            int index, Action<GenericBooleanArrayState> operation)
        {
            var owner = scenario.MissingOwner ? null : new GenericBooleanArrayState
            {
                Before = -41, Values = Copy(scenario.Initial), After = 73
            };
            var originalArray = owner == null ? null : owner.Values;
            var alias = owner == null ? null : new GenericBooleanArrayState
            {
                Before = 17, Values = originalArray, After = -19
            };
            var before = Copy(originalArray);
            var exception = "none";
            try { operation(owner); }
            catch (Exception error) { exception = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "case", scenario.Label }, { "index", index },
                { "exception", exception }, { "before", before },
                { "after", Copy(owner == null ? null : owner.Values) },
                { "aliasAfter", Copy(alias == null ? null : alias.Values) },
                { "sameFieldReference", owner != null && ReferenceEquals(owner.Values, originalArray) },
                { "aliasSharesArray", originalArray != null && alias != null &&
                    ReferenceEquals(alias.Values, originalArray) },
                { "ownerBefore", owner == null ? (object)null : owner.Before },
                { "ownerAfter", owner == null ? (object)null : owner.After },
                { "aliasBefore", alias == null ? (object)null : alias.Before },
                { "aliasAfterField", alias == null ? (object)null : alias.After }
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
