using System;
using System.Collections.Generic;
using System.IO;
using StringReferenceStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // This driver is outside the recovery scope.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();

            Record(observations, "new-value", CreateHolder(null), new string('a', 5));
            Record(observations, "overwrite", CreateHolder("old"), new string('n', 4));
            Record(observations, "clear", CreateHolder("old"), null);
            Record(observations, "already-null", CreateHolder(null), null);
            Record(observations, "empty", CreateHolder("old"), string.Empty);
            Record(observations, "unicode", CreateHolder("old"), "café-雪");

            var same = new string('s', 3);
            Record(observations, "same-reference", CreateHolder(same), same);

            var sequential = CreateHolder("seed");
            Record(observations, "sequence-first", sequential, new string('f', 5));
            Record(observations, "sequence-second", sequential, new string('s', 6));

            Record(observations, "null-owner-value", null, new string('z', 2));
            Record(observations, "null-owner-null", null, null);
            Record(observations, "null-owner-empty", null, string.Empty);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "string-reference-store" },
                { "observations", observations }
            }));
        }

        private static TextHolder CreateHolder(string previous)
        {
            return new TextHolder
            {
                Prefix = new object(),
                Text = previous,
                Suffix = new object(),
                Sentinel = 73
            };
        }

        private static void Record(List<object> observations, string kind, TextHolder holder, string value)
        {
            var before = holder == null ? null : holder.Text;
            var prefix = holder == null ? null : holder.Prefix;
            var suffix = holder == null ? null : holder.Suffix;
            int? sentinelBefore = holder == null ? (int?)null : holder.Sentinel;
            var exception = "none";
            try { StringReferenceStores.StoreText(holder, value); }
            catch (Exception error) { exception = error.GetType().FullName; }
            var after = holder == null ? null : holder.Text;

            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "exception", exception },
                { "value", value },
                { "textBefore", before },
                { "textAfter", after },
                { "textSameValue", holder == null ? (object)null : ReferenceEquals(after, value) },
                { "textSameBefore", holder == null ? (object)null : ReferenceEquals(after, before) },
                { "prefixSame", holder == null ? (object)null : ReferenceEquals(holder.Prefix, prefix) },
                { "suffixSame", holder == null ? (object)null : ReferenceEquals(holder.Suffix, suffix) },
                { "sentinelBefore", sentinelBefore },
                { "sentinelAfter", holder == null ? (int?)null : holder.Sentinel }
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
