using System;
using System.Collections.Generic;
using System.IO;
using ArraySequenceFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // This driver is outside the assembly selected for recovery.
    public static class BehaviorProbe
    {
        private sealed class Scenario
        {
            public readonly string Label;
            public readonly bool MissingOwner;
            public readonly int[] Initial;
            public readonly bool ShareAlias;
            public readonly int InitialCounter;

            public Scenario(string label, bool missingOwner, int[] initial, bool shareAlias,
                int initialCounter = 23)
            {
                Label = label;
                MissingOwner = missingOwner;
                Initial = initial;
                ShareAlias = shareAlias;
                InitialCounter = initialCounter;
            }
        }

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var scenarios = new[]
            {
                new Scenario("null-owner", true, null, false),
                new Scenario("null-array", false, null, false),
                new Scenario("empty-shared", false, new int[0], true),
                new Scenario("single-shared", false, new[] { 17 }, true),
                new Scenario("mixed-shared", false,
                    new[] { int.MinValue, -7, int.MaxValue }, true),
                new Scenario("mixed-distinct", false,
                    new[] { int.MinValue, -7, int.MaxValue }, false)
            };

            foreach (var scenario in scenarios)
            {
                var length = scenario.Initial == null ? 0 : scenario.Initial.Length;
                Record(observations, scenario, "same-index", 0, 0);
                Record(observations, scenario, "last-to-first", length - 1, 0);
                Record(observations, scenario, "first-to-last", 0, length - 1);
                Record(observations, scenario, "negative-first", -1, 0);
                Record(observations, scenario, "negative-second", 0, -1);
                Record(observations, scenario, "upper-first", length, 0);
                Record(observations, scenario, "upper-second", 0, length);
                Record(observations, scenario, "both-negative", -1, -1);
                Record(observations, scenario, "both-upper", length, length);
                Record(observations, scenario, "minimum-first", int.MinValue, int.MaxValue);
                Record(observations, scenario, "maximum-second", 0, int.MaxValue);
            }
            Record(observations, new Scenario("counter-overflow-second-fails", false,
                new[] { 17 }, true, int.MaxValue), "upper-second-after-overflow", 0, 1);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "array-sequence" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, Scenario scenario,
            string kind, int firstIndex, int secondIndex)
        {
            var owner = scenario.MissingOwner ? null : new ArraySequence
            {
                Values = Copy(scenario.Initial), Counter = scenario.InitialCounter, LastRead = -999
            };
            var originalArray = owner == null ? null : owner.Values;
            var alias = owner == null ? null : new ArraySequence
            {
                Values = scenario.ShareAlias ? originalArray : Copy(originalArray),
                Counter = 777,
                LastRead = -777
            };
            var before = Copy(originalArray);
            var aliasBefore = Copy(alias == null ? null : alias.Values);

            object result = null;
            var exception = "none";
            try { result = owner.ReadThenWrite(firstIndex, secondIndex); }
            catch (Exception error) { exception = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "case", scenario.Label },
                { "firstIndex", firstIndex }, { "secondIndex", secondIndex },
                { "result", result }, { "exception", exception },
                { "before", before },
                { "after", Copy(owner == null ? null : owner.Values) },
                { "aliasBefore", aliasBefore },
                { "aliasAfter", Copy(alias == null ? null : alias.Values) },
                { "counterBefore", owner == null ? (object)null : scenario.InitialCounter },
                { "counterAfter", owner == null ? (object)null : owner.Counter },
                { "lastReadBefore", owner == null ? (object)null : -999 },
                { "lastReadAfter", owner == null ? (object)null : owner.LastRead },
                { "aliasCounterAfter", alias == null ? (object)null : alias.Counter },
                { "aliasLastReadAfter", alias == null ? (object)null : alias.LastRead },
                { "sameFieldReference", owner != null &&
                    ReferenceEquals(owner.Values, originalArray) },
                { "aliasSharesArray", originalArray != null && alias != null &&
                    ReferenceEquals(alias.Values, originalArray) }
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
