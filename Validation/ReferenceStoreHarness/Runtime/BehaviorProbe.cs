using System;
using System.Collections.Generic;
using System.IO;
using ReferenceStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // Independent driver; none of its methods belong to the recovery scope.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();

            Record(observations, "new-value", CreateOwner(null), new ReferenceNode { Marker = 41 });
            Record(observations, "replace-value", CreateOwner(new ReferenceNode { Marker = 43 }),
                new ReferenceNode { Marker = 47 });
            Record(observations, "clear-value", CreateOwner(new ReferenceNode { Marker = 53 }), null);
            Record(observations, "already-null", CreateOwner(null), null);

            var existing = new ReferenceNode { Marker = 59 };
            Record(observations, "same-value", CreateOwner(existing), existing);

            var selfOwner = CreateOwner(new ReferenceNode { Marker = 61 });
            Record(observations, "owner-as-value", selfOwner, selfOwner);

            Record(observations, "null-owner-value", null, new ReferenceNode { Marker = 67 });
            Record(observations, "null-owner-null", null, null);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "reference-store" },
                { "observations", observations }
            }));
        }

        private static ReferenceNode CreateOwner(ReferenceNode previous)
        {
            return new ReferenceNode
            {
                Prefix = new ReferenceNode { Marker = -11 },
                Next = previous,
                Suffix = new ReferenceNode { Marker = 13 },
                Marker = 29
            };
        }

        private static void Record(List<object> observations, string kind,
            ReferenceNode owner, ReferenceNode value)
        {
            var previous = owner == null ? null : owner.Next;
            var prefix = owner == null ? null : owner.Prefix;
            var suffix = owner == null ? null : owner.Suffix;
            var exception = "none";
            try { ReferenceStores.Store(owner, value); }
            catch (Exception error) { exception = error.GetType().FullName; }

            var next = owner == null ? null : owner.Next;
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "exception", exception },
                { "nextIsNull", owner == null ? (object)null : next == null },
                { "nextSameValue", owner == null ? (object)null : ReferenceEquals(next, value) },
                { "nextSamePrevious", owner == null ? (object)null : ReferenceEquals(next, previous) },
                { "nextSameOwner", owner == null ? (object)null : ReferenceEquals(next, owner) },
                { "nextMarker", next == null ? (object)null : next.Marker },
                { "previousMarker", previous == null ? (object)null : previous.Marker },
                { "valueMarker", value == null ? (object)null : value.Marker },
                { "prefixSame", owner == null ? (object)null : ReferenceEquals(owner.Prefix, prefix) },
                { "suffixSame", owner == null ? (object)null : ReferenceEquals(owner.Suffix, suffix) },
                { "prefixMarker", prefix == null ? (object)null : prefix.Marker },
                { "suffixMarker", suffix == null ? (object)null : suffix.Marker },
                { "ownerMarker", owner == null ? (object)null : owner.Marker }
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
