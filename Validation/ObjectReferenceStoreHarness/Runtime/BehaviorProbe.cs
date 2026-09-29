using System;
using System.Collections.Generic;
using System.IO;
using ObjectReferenceStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // This driver is outside the recovery scope.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();

            Record(observations, "new-object", CreateHolder(null), new object());
            Record(observations, "overwrite-object", CreateHolder("old"), new object());
            Record(observations, "store-string", CreateHolder(new object()), "text-雪");
            Record(observations, "store-boxed-int", CreateHolder("old"), (object)23);
            Record(observations, "clear", CreateHolder(new object()), null);
            Record(observations, "already-null", CreateHolder(null), null);

            var same = new object();
            Record(observations, "same-reference", CreateHolder(same), same);

            var sequential = CreateHolder("seed");
            Record(observations, "sequence-first", sequential, (object)17);
            Record(observations, "sequence-second", sequential, "last");

            Record(observations, "null-owner-object", null, new object());
            Record(observations, "null-owner-null", null, null);
            Record(observations, "null-owner-string", null, "text");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "object-reference-store" },
                { "observations", observations }
            }));
        }

        private static ObjectHolder CreateHolder(object previous)
        {
            return new ObjectHolder
            {
                Prefix = new object(),
                Item = previous,
                Suffix = new object(),
                Sentinel = 61
            };
        }

        private static void Record(List<object> observations, string kind, ObjectHolder holder, object value)
        {
            var before = holder == null ? null : holder.Item;
            var prefix = holder == null ? null : holder.Prefix;
            var suffix = holder == null ? null : holder.Suffix;
            int? sentinelBefore = holder == null ? (int?)null : holder.Sentinel;
            var exception = "none";
            try { ObjectReferenceStores.StoreObject(holder, value); }
            catch (Exception error) { exception = error.GetType().FullName; }
            var after = holder == null ? null : holder.Item;

            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "exception", exception },
                { "value", Describe(value) },
                { "itemBefore", Describe(before) },
                { "itemAfter", Describe(after) },
                { "itemSameValue", holder == null ? (object)null : ReferenceEquals(after, value) },
                { "itemSameBefore", holder == null ? (object)null : ReferenceEquals(after, before) },
                { "prefixSame", holder == null ? (object)null : ReferenceEquals(holder.Prefix, prefix) },
                { "suffixSame", holder == null ? (object)null : ReferenceEquals(holder.Suffix, suffix) },
                { "sentinelBefore", sentinelBefore },
                { "sentinelAfter", holder == null ? (int?)null : holder.Sentinel }
            });
        }

        private static string Describe(object value)
        {
            if (value == null)
                return "null";
            if (value is string text)
                return "string:" + text;
            if (value is int number)
                return "int:" + number;
            return value.GetType().FullName;
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
