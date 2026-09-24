using System;
using System.Collections.Generic;
using System.IO;
using LiteralConcatFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var node = new DerivedTextNode
            {
                Text = new string('r', 3), Marker = int.MinValue,
                DerivedMarker = int.MaxValue
            };
            var resolver = new Resolver { Current = node, Neighbor = -41 };
            var dispatchNode = new DerivedTextNode
            {
                Text = "dispatch", Marker = 11, DerivedMarker = 13
            };
            var further = new FurtherResolver
            {
                Current = dispatchNode, Neighbor = 17, Extra = 43
            };
            var observations = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "constructors" }, { "nodeCreated", node != null },
                    { "resolverCreated", resolver != null },
                    { "derivedResolverCreated", further != null }
                }
            };

            observations.Add(Observe("first", resolver, () => resolver.Compose()));
            observations.Add(Observe("repeat", resolver, () => resolver.Compose()));
            node.Text = null;
            observations.Add(Observe("null-text", resolver, () => resolver.Compose()));
            node.Text = string.Empty;
            observations.Add(Observe("empty-text", resolver, () => resolver.Compose()));
            node.Text = "\u03A9";
            observations.Add(Observe("unicode-text", resolver, () => resolver.Compose()));
            resolver.Current = null;
            observations.Add(Observe("null-node", resolver, () => resolver.Compose()));
            resolver.Current = node;
            Resolver missing = null;
            observations.Add(Observe("null-owner", null, () => missing.Compose()));
            Resolver dispatch = further;
            observations.Add(Observe("virtual-dispatch", dispatch, () => dispatch.Compose()));
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "neighbors" },
                { "nodeMarker", node.Marker }, { "derivedNodeMarker", node.DerivedMarker },
                { "resolverNeighbor", resolver.Neighbor }, { "sameNode", ReferenceEquals(resolver.Current, node) },
                { "dispatchNodeMarker", dispatchNode.Marker },
                { "dispatchDerivedMarker", dispatchNode.DerivedMarker },
                { "dispatchNeighbor", further.Neighbor }, { "dispatchExtra", further.Extra },
                { "sameDispatchNode", ReferenceEquals(further.Current, dispatchNode) }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "literal-concat" },
                { "observations", observations }
            }));
        }

        private static object Observe(string kind, Resolver owner, Func<string> call)
        {
            var before = owner == null ? null : owner.Current;
            string result = null;
            var exception = "none";
            try { result = call(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            var after = owner == null ? null : owner.Current;
            var derived = owner as FurtherResolver;
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "result", result }, { "exception", exception },
                { "sameNode", owner != null && ReferenceEquals(before, after) },
                { "textAfter", after == null ? null : after.Text },
                { "nodeMarkerAfter", after == null ? (int?)null : after.Marker },
                { "derivedMarkerAfter", after == null ? (int?)null : after.DerivedMarker },
                { "neighborAfter", owner == null ? (int?)null : owner.Neighbor },
                { "extraAfter", derived == null ? (int?)null : derived.Extra }
            };
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
