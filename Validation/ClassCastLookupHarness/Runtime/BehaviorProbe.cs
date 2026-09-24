using System;
using System.Collections.Generic;
using System.IO;
using ClassCastLookupFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var plain = new BaseNode { Label = "plain", Marker = 11 };
            var derived = new DerivedNode
            {
                Label = new string('d', 2), Marker = -17, Detail = 23
            };
            var further = new FurtherNode
            {
                Label = "further", Marker = 31, Detail = 37, Extra = 41
            };
            var owner = new Resolver { Neighbor = 43 };
            var alias = new Resolver { Current = derived, Neighbor = -59 };
            var observations = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "constructors" }, { "plain", plain != null },
                    { "derived", derived != null }, { "further", further != null },
                    { "owner", owner != null }, { "alias", alias != null }
                }
            };

            Observe(observations, "null", owner);
            owner.Current = plain;
            Observe(observations, "incompatible", owner);
            owner.Current = derived;
            Observe(observations, "exact", owner);
            Observe(observations, "repeat", owner);
            owner.Current = further;
            Observe(observations, "subclass", owner);
            owner.Current = derived;
            Observe(observations, "alias-first", owner);
            Observe(observations, "alias-second", alias);
            derived.Label = "changed";
            Observe(observations, "changed-first", owner);
            Observe(observations, "changed-second", alias);
            Resolver missing = null;
            Observe(observations, "null-owner", missing);

            observations.Add(new Dictionary<string, object>
            {
                { "kind", "neighbors" }, { "plainMarker", plain.Marker },
                { "derivedMarker", derived.Marker }, { "derivedDetail", derived.Detail },
                { "furtherMarker", further.Marker }, { "furtherDetail", further.Detail },
                { "furtherExtra", further.Extra }, { "ownerNeighbor", owner.Neighbor },
                { "aliasNeighbor", alias.Neighbor },
                { "sameAlias", ReferenceEquals(owner.Current, alias.Current) }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "class-cast-lookup" },
                { "observations", observations }
            }));
        }

        private static void Observe(List<object> observations, string label, Resolver owner)
        {
            var before = owner == null ? null : owner.Current;
            DerivedNode result = null;
            var exception = "none";
            try { result = owner.Lookup(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            var after = owner == null ? null : owner.Current;
            var derived = after as DerivedNode;
            var further = after as FurtherNode;
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "lookup:" + label },
                { "resultType", result == null ? null : result.GetType().Name },
                { "sameResultAsCurrent", result != null && ReferenceEquals(result, before) },
                { "exception", exception },
                { "sameCurrentAfter", owner != null && ReferenceEquals(before, after) },
                { "currentType", after == null ? null : after.GetType().Name },
                { "labelAfter", after == null ? null : after.Label },
                { "markerAfter", after == null ? (int?)null : after.Marker },
                { "detailAfter", derived == null ? (int?)null : derived.Detail },
                { "extraAfter", further == null ? (int?)null : further.Extra },
                { "neighborAfter", owner == null ? (int?)null : owner.Neighbor }
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
