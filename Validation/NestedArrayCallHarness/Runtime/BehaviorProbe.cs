using System;
using System.Collections.Generic;
using System.IO;
using NestedArrayCallFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly string[] Owners =
            { "NestedA", "NestedB", "NestedC", "DirectA", "DirectB" };
        private static readonly int[] EdgeIndices = { -1, 0, 3 };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var owner in Owners)
            {
                for (var length = 0; length <= 3; length++)
                    for (var index = -1; index <= 3; index++)
                        observations.Add(Observe(owner, "length-" + length,
                            length, index));
                foreach (var index in EdgeIndices)
                    observations.Add(Observe(owner, "array-null", null, index));
                observations.Add(Observe(owner, "element-null", 2, 0));
                observations.Add(Observe(owner, "link-null", 2, 0));
                foreach (var index in EdgeIndices)
                    observations.Add(Observe(owner, "owner-null", null, index));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "nested-array-call" },
                { "observations", observations }
            }));
        }

        private static object Observe(string ownerKind, string kind,
            int? length, int index)
        {
            var nodes = kind == "array-null" || kind == "owner-null"
                ? null : CreateNodes(length.Value, kind);
            var owner = kind == "owner-null" ? null : CreateOwner(ownerKind, nodes);
            var elements = nodes == null ? null : (Node[])nodes.Clone();
            Leaf[] links = null;
            if (nodes != null)
            {
                links = new Leaf[nodes.Length];
                for (var item = 0; item < nodes.Length; item++)
                    links[item] = nodes[item] == null ? null : nodes[item].Link;
            }
            var exception = "none";
            try { Invoke(ownerKind, owner, index); }
            catch (Exception error) { exception = error.GetType().FullName; }
            var direct = new List<object>();
            var nested = new List<object>();
            if (nodes != null)
                foreach (var node in nodes)
                {
                    direct.Add(node == null ? null : (object)node.Touched);
                    nested.Add(node == null || node.Link == null
                        ? null : (object)node.Link.Touched);
                }
            var sameElements = true;
            var sameLinks = true;
            if (nodes != null)
                for (var item = 0; item < nodes.Length; item++)
                {
                    sameElements &= ReferenceEquals(nodes[item], elements[item]);
                    sameLinks &= ReferenceEquals(nodes[item] == null
                        ? null : nodes[item].Link, links[item]);
                }
            return new Dictionary<string, object>
            {
                { "owner", ownerKind }, { "kind", kind },
                { "length", length }, { "index", index },
                { "exception", exception },
                { "directTouched", direct }, { "nestedTouched", nested },
                { "sameArray", owner == null ? null :
                    (object)ReferenceEquals(nodes, GetNodes(ownerKind, owner)) },
                { "sameElements", nodes == null ? null : (object)sameElements },
                { "sameLinks", nodes == null ? null : (object)sameLinks },
                { "before", owner == null ? null : (object)GetBefore(ownerKind, owner) },
                { "spacer", owner == null || ownerKind != "NestedC"
                    ? null : (object)((NestedC)owner).Spacer },
                { "after", owner == null ? null : (object)GetAfter(ownerKind, owner) }
            };
        }

        private static Node[] CreateNodes(int length, string kind)
        {
            var result = new Node[length];
            for (var item = 0; item < length; item++)
                result[item] = new Node { Link = new Leaf() };
            if (kind == "element-null") result[0] = null;
            if (kind == "link-null") result[0].Link = null;
            return result;
        }

        private static object CreateOwner(string kind, Node[] nodes)
        {
            switch (kind)
            {
                case "NestedA": return new NestedA
                    { Before = -53, Nodes = nodes, After = 59 };
                case "NestedB": return new NestedB
                    { Before = -53, Nodes = nodes, After = 59 };
                case "NestedC": return new NestedC
                    { Before = -53, Spacer = -61, Nodes = nodes, After = 59 };
                case "DirectA": return new DirectA
                    { Before = -53, Nodes = nodes, After = 59 };
                default: return new DirectB
                    { Before = -53, Nodes = nodes, After = 59 };
            }
        }

        private static void Invoke(string kind, object owner, int index)
        {
            switch (kind)
            {
                case "NestedA": ((NestedA)owner).TouchAt(index); break;
                case "NestedB": ((NestedB)owner).TouchAt(index); break;
                case "NestedC": ((NestedC)owner).TouchAt(index); break;
                case "DirectA": ((DirectA)owner).TouchAt(index); break;
                default: ((DirectB)owner).TouchAt(index); break;
            }
        }

        private static Node[] GetNodes(string kind, object owner)
        {
            switch (kind)
            {
                case "NestedA": return ((NestedA)owner).Nodes;
                case "NestedB": return ((NestedB)owner).Nodes;
                case "NestedC": return ((NestedC)owner).Nodes;
                case "DirectA": return ((DirectA)owner).Nodes;
                default: return ((DirectB)owner).Nodes;
            }
        }

        private static long GetBefore(string kind, object owner)
        {
            switch (kind)
            {
                case "NestedA": return ((NestedA)owner).Before;
                case "NestedB": return ((NestedB)owner).Before;
                case "NestedC": return ((NestedC)owner).Before;
                case "DirectA": return ((DirectA)owner).Before;
                default: return ((DirectB)owner).Before;
            }
        }

        private static long GetAfter(string kind, object owner)
        {
            switch (kind)
            {
                case "NestedA": return ((NestedA)owner).After;
                case "NestedB": return ((NestedB)owner).After;
                case "NestedC": return ((NestedC)owner).After;
                case "DirectA": return ((DirectA)owner).After;
                default: return ((DirectB)owner).After;
            }
        }

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
