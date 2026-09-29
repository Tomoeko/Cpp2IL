using System;
using System.Collections.Generic;
using System.IO;
using FixedBooleanConjunctionFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            Record(observations, "both-false", NewPair(new[] { false }, new[] { false }));
            Record(observations, "second-true", NewPair(new[] { false }, new[] { true }));
            Record(observations, "first-true", NewPair(new[] { true }, new[] { false }));
            Record(observations, "both-true", NewPair(new[] { true }, new[] { true }));
            Record(observations, "skip-second-null", NewPair(new[] { true }, null));
            Record(observations, "skip-second-empty", NewPair(new[] { true }, Array.Empty<bool>()));
            Record(observations, "first-null", NewPair(null, new[] { false }));
            Record(observations, "first-null-second-empty", NewPair(null, Array.Empty<bool>()));
            Record(observations, "first-empty", NewPair(Array.Empty<bool>(), new[] { false }));
            Record(observations, "first-empty-second-null", NewPair(Array.Empty<bool>(), null));
            Record(observations, "second-null", NewPair(new[] { false }, null));
            Record(observations, "second-empty", NewPair(new[] { false }, Array.Empty<bool>()));

            var shared = new[] { false };
            var aliased = NewPair(shared, shared);
            Record(observations, "alias-false", aliased);
            shared[0] = true;
            Record(observations, "alias-true", aliased);
            Record(observations, "owner-null", null);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "fixed-boolean-conjunction" },
                { "observations", observations }
            }));
        }

        private static BooleanPair NewPair(bool[] first, bool[] second)
        {
            return new BooleanPair { First = first, Second = second, Sentinel = 73 };
        }

        private static bool[] Snapshot(bool[] values)
        {
            return values == null ? null : (bool[])values.Clone();
        }

        private static void Record(List<object> observations, string kind, BooleanPair owner)
        {
            var first = owner == null ? null : owner.First;
            var second = owner == null ? null : owner.Second;
            var firstBefore = Snapshot(first);
            var secondBefore = Snapshot(second);
            int? sentinelBefore = owner == null ? (int?)null : owner.Sentinel;
            bool? result = null;
            var exception = "none";
            try { result = owner.BothFalseAtZero(); }
            catch (Exception error) { exception = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "result", result },
                { "exception", exception },
                { "firstBefore", firstBefore },
                { "secondBefore", secondBefore },
                { "firstAfter", Snapshot(owner == null ? null : owner.First) },
                { "secondAfter", Snapshot(owner == null ? null : owner.Second) },
                { "firstSecondSame", owner == null ? (bool?)null : ReferenceEquals(first, second) },
                { "firstFieldSame", owner == null ? (bool?)null : ReferenceEquals(first, owner.First) },
                { "secondFieldSame", owner == null ? (bool?)null : ReferenceEquals(second, owner.Second) },
                { "sentinelBefore", sentinelBefore },
                { "sentinelAfter", owner == null ? (int?)null : owner.Sentinel }
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
