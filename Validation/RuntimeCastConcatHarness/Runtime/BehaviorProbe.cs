using System;
using System.Collections.Generic;
using System.IO;
using RuntimeCastConcatFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var plain = new BaseNode { Text = "plain", Marker = 11 };
            var derived = new DerivedNode
            {
                Text = new string('d', 2), Marker = -17, Detail = 23
            };
            var further = new FurtherNode
            {
                Text = "further", Marker = 31, Detail = 37, Extra = 41
            };
            var resolver = new Resolver { Neighbor = 43 };
            var alias = new Resolver { Current = derived, Neighbor = -59 };
            var dispatchOwner = new FurtherResolver
            {
                Current = further, Neighbor = 47, Extra = 53
            };

            var observations = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "constructors" }, { "plain", plain != null },
                    { "derived", derived != null }, { "further", further != null },
                    { "resolver", resolver != null }, { "alias", alias != null },
                    { "dispatchOwner", dispatchOwner != null }
                }
            };
            ObserveLiteral(observations, "null", null);
            ObserveLiteral(observations, "first", new string('v', 2));
            ObserveLiteral(observations, "repeat", new string('v', 2));
            ObserveLiteral(observations, "unicode", "\u03A9");

            var targetType = MetadataControls.TargetType();
            var repeatedType = MetadataControls.TargetType();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "target-type" },
                { "name", targetType == null ? null : targetType.FullName },
                { "matchesManagedType", targetType == typeof(DerivedNode) },
                { "sameRepeatedType", ReferenceEquals(targetType, repeatedType) }
            });

            ObserveLookup(observations, "null", resolver);
            ObserveCompose(observations, "null", resolver);
            resolver.Current = plain;
            ObserveLookup(observations, "incompatible", resolver);
            ObserveCompose(observations, "incompatible", resolver);
            resolver.Current = derived;
            ObserveLookup(observations, "exact", resolver);
            ObserveCompose(observations, "exact", resolver);
            ObserveCompose(observations, "repeat", resolver);
            derived.Text = null;
            ObserveCompose(observations, "null-text", resolver);
            derived.Text = "dd";
            resolver.Current = further;
            ObserveLookup(observations, "subclass", resolver);
            ObserveCompose(observations, "subclass", resolver);
            resolver.Current = derived;
            ObserveLookup(observations, "alias-first", resolver);
            ObserveLookup(observations, "alias-second", alias);
            derived.Text = "changed";
            ObserveCompose(observations, "alias-first", resolver);
            ObserveCompose(observations, "alias-second", alias);
            Resolver dispatched = dispatchOwner;
            ObserveCompose(observations, "virtual-dispatch", dispatched);
            Resolver missing = null;
            ObserveCompose(observations, "null-owner", missing);

            observations.Add(new Dictionary<string, object>
            {
                { "kind", "neighbors" }, { "plainMarker", plain.Marker },
                { "derivedMarker", derived.Marker }, { "derivedDetail", derived.Detail },
                { "furtherMarker", further.Marker }, { "furtherDetail", further.Detail },
                { "furtherExtra", further.Extra }, { "resolverNeighbor", resolver.Neighbor },
                { "aliasNeighbor", alias.Neighbor }, { "dispatchNeighbor", dispatchOwner.Neighbor },
                { "dispatchExtra", dispatchOwner.Extra },
                { "sameAlias", ReferenceEquals(resolver.Current, alias.Current) },
                { "sameDispatch", ReferenceEquals(dispatchOwner.Current, further) }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "runtime-cast-concat" },
                { "observations", observations }
            }));
        }

        private static void ObserveLiteral(List<object> observations, string label, string value)
        {
            string result = null;
            var exception = "none";
            try { result = MetadataControls.AppendLiteral(value); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "literal:" + label }, { "result", result },
                { "exception", exception }
            });
        }

        private static void ObserveLookup(List<object> observations, string label,
            ResolverBase owner)
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
                { "textAfter", after == null ? null : after.Text },
                { "markerAfter", after == null ? (int?)null : after.Marker },
                { "detailAfter", derived == null ? (int?)null : derived.Detail },
                { "extraAfter", further == null ? (int?)null : further.Extra },
                { "neighborAfter", owner == null ? (int?)null : owner.Neighbor }
            });
        }

        private static void ObserveCompose(List<object> observations, string label,
            Resolver owner)
        {
            var before = owner == null ? null : owner.Current;
            string result = null;
            var exception = "none";
            try { result = owner.Compose(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            var after = owner == null ? null : owner.Current;
            var derived = after as DerivedNode;
            var further = after as FurtherNode;
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "compose:" + label }, { "result", result },
                { "exception", exception },
                { "sameCurrentAfter", owner != null && ReferenceEquals(before, after) },
                { "currentType", after == null ? null : after.GetType().Name },
                { "textAfter", after == null ? null : after.Text },
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
