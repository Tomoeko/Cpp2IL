using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GuardedArrayOperationsFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        public static void Write(string path, string stage)
        {
            var rows = new List<object> { Declarations() };
            var fresh = new ArrayOperations();
            var freshNode = new ArrayNode();
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "constructors" }, { "valuesNull", fresh.Values == null },
                { "nodesNull", fresh.Nodes == null }, { "counter", fresh.Counter },
                { "observed", fresh.Observed }, { "nodeValue", freshNode.ReadValue() },
                { "nodeCalls", freshNode.Calls }
            });

            foreach (var index in new[] { int.MinValue, -1, 0, 1, 2, 3, int.MaxValue })
                Scalar(rows, "twice-" + index, "twice", new[] { 4, -7, int.MaxValue }, index, 0, 0, false);
            Scalar(rows, "twice-empty", "twice", new int[0], 0, 0, 0, false);
            Scalar(rows, "twice-null", "twice", null, 0, 0, 0, false);
            Scalar(rows, "twice-null-holder", "twice", new[] { 4, -7, int.MaxValue }, 0, 0, 0, true);
            Scalar(rows, "set-distinct", "set", new[] { 4, -7, int.MaxValue }, 0, 2, 99, false);
            Scalar(rows, "set-same", "set", new[] { 4, -7, int.MaxValue }, 1, 1, -44, false);
            Scalar(rows, "set-write-failure", "set", new[] { 4, -7, int.MaxValue }, 0, 3, 99, false);
            Scalar(rows, "set-read-failure", "set", new[] { 4, -7, int.MaxValue }, 3, 0, 99, false);
            Scalar(rows, "set-negative-write", "set", new[] { 4, -7, int.MaxValue }, 2, -1, 99, false);
            Scalar(rows, "set-null", "set", null, 0, 0, 99, false);
            Scalar(rows, "set-null-holder", "set", new[] { 4, -7, int.MaxValue }, 0, 0, 99, true);
            Scalar(rows, "mark", "mark", new[] { 4, -7, int.MaxValue }, 0, 0, 0, false);

            foreach (var index in new[] { -1, 0, 1, 2, 3 })
                Sum(rows, "sum-" + index, new[] { 4, -7, int.MaxValue }, new[] { 6, 8, 1 }, index);
            var same = new[] { 4, -7, int.MaxValue };
            Sum(rows, "sum-alias", same, same, 1);
            Sum(rows, "sum-null-left", null, new int[0], 0);
            Sum(rows, "sum-null-right", new[] { 4 }, null, 0);
            Sum(rows, "sum-left-bounds-before-null-right", new[] { 4 }, null, 1);
            Sum(rows, "sum-right-empty", new[] { 4 }, new int[0], 0);

            var sharedNode = new ArrayNode { Value = 7, Calls = 2 };
            var nodes = new[] { sharedNode, null, sharedNode };
            Node(rows, "node-first", nodes, sharedNode, 0, 11, false);
            Node(rows, "node-alias", nodes, sharedNode, 2, -9, false);
            Node(rows, "node-null-element", nodes, sharedNode, 1, 22, false);
            Node(rows, "node-negative", nodes, sharedNode, -1, 22, false);
            Node(rows, "node-bounds", nodes, sharedNode, 3, 22, false);
            Node(rows, "node-empty", new ArrayNode[0], sharedNode, 0, 22, false);
            Node(rows, "node-null-array", null, sharedNode, 0, 22, false);
            Node(rows, "node-null-holder", nodes, sharedNode, 0, 22, true);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "guarded-array-operations" }, { "observations", rows }
            }));
        }

        private static object Declarations()
        {
            var owner = typeof(ArrayOperations);
            var node = typeof(ArrayNode);
            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "owner", owner.IsPublic && owner.IsSealed && owner.BaseType == typeof(object) &&
                    owner.GetFields(Declared).Length == 4 && owner.GetMethods(Declared).Length == 5 &&
                    Constructor(owner) && Field(owner, "Values", typeof(int[])) &&
                    Field(owner, "Nodes", typeof(ArrayNode[])) && Field(owner, "Counter", typeof(int)) &&
                    Field(owner, "Observed", typeof(int)) },
                { "node", node.IsPublic && node.IsSealed && node.BaseType == typeof(object) &&
                    node.GetFields(Declared).Length == 2 && node.GetMethods(Declared).Length == 2 &&
                    Constructor(node) && Field(node, "Value", typeof(int)) && Field(node, "Calls", typeof(int)) },
                { "signatures", Signature(owner, "ReadTwiceAfterEffect", false, typeof(int), typeof(int)) &&
                    Signature(owner, "Mark", false, typeof(void)) &&
                    Signature(owner, "TouchNode", false, typeof(void), typeof(int), typeof(int)) &&
                    Signature(owner, "ReadThenSet", false, typeof(int), typeof(int), typeof(int), typeof(int)) &&
                    Signature(owner, "Sum", true, typeof(int), typeof(int[]), typeof(int[]), typeof(int)) &&
                    Signature(node, "ReadValue", false, typeof(int)) &&
                    Signature(node, "SetValue", false, typeof(void), typeof(int)) }
            };
        }

        private static bool Constructor(Type type)
        {
            var constructors = type.GetConstructors(Declared);
            return constructors.Length == 1 && constructors[0].IsPublic &&
                !constructors[0].IsStatic && constructors[0].GetParameters().Length == 0;
        }

        private static bool Field(Type type, string name, Type valueType)
        {
            var field = type.GetField(name, Declared);
            return field != null && field.IsPublic && !field.IsStatic && field.FieldType == valueType;
        }

        private static bool Signature(Type type, string name, bool isStatic, Type result, params Type[] arguments)
        {
            var method = type.GetMethod(name, Declared);
            if (method == null || !method.IsPublic || method.IsStatic != isStatic ||
                method.IsVirtual || method.IsGenericMethod || method.ReturnType != result) return false;
            var parameters = method.GetParameters();
            if (parameters.Length != arguments.Length) return false;
            for (var index = 0; index < parameters.Length; index++)
                if (parameters[index].ParameterType != arguments[index]) return false;
            return true;
        }

        private static void Scalar(List<object> rows, string kind, string operation, int[] witness,
            int read, int write, int value, bool nullHolder)
        {
            var owner = nullHolder ? null : new ArrayOperations { Values = witness, Counter = 10, Observed = 17 };
            var alias = new ArrayOperations { Values = witness, Counter = 31, Observed = 37 };
            var before = Snapshot(witness);
            object result = null;
            var exception = "none";
            try
            {
                if (operation == "twice") result = owner.ReadTwiceAfterEffect(read);
                else if (operation == "set") result = owner.ReadThenSet(read, write, value);
                else owner.Mark();
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", operation }, { "read", read }, { "write", write },
                { "value", value }, { "result", result }, { "exception", exception },
                { "before", before }, { "after", Snapshot(witness) },
                { "counter", owner == null ? null : (object)owner.Counter },
                { "observed", owner == null ? null : (object)owner.Observed },
                { "sameArray", owner == null ? null : (object)ReferenceEquals(owner.Values, witness) },
                { "aliasSame", ReferenceEquals(alias.Values, witness) }, { "aliasAfter", Snapshot(alias.Values) },
                { "aliasCounter", alias.Counter }, { "aliasObserved", alias.Observed }
            });
        }

        private static void Sum(List<object> rows, string kind, int[] left, int[] right, int index)
        {
            var leftBefore = Snapshot(left);
            var rightBefore = Snapshot(right);
            object result = null;
            var exception = "none";
            try { result = ArrayOperations.Sum(left, right, index); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "index", index }, { "result", result }, { "exception", exception },
                { "leftBefore", leftBefore }, { "leftAfter", Snapshot(left) },
                { "rightBefore", rightBefore }, { "rightAfter", Snapshot(right) },
                { "sameArray", ReferenceEquals(left, right) }
            });
        }

        private static void Node(List<object> rows, string kind, ArrayNode[] nodes, ArrayNode witness,
            int index, int value, bool nullHolder)
        {
            var owner = nullHolder ? null : new ArrayOperations { Nodes = nodes, Counter = 10, Observed = 17 };
            var beforeValues = NodeValues(nodes, false);
            var beforeCalls = NodeValues(nodes, true);
            var exception = "none";
            try { owner.TouchNode(index, value); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "index", index }, { "value", value }, { "exception", exception },
                { "beforeValues", beforeValues }, { "afterValues", NodeValues(nodes, false) },
                { "beforeCalls", beforeCalls }, { "afterCalls", NodeValues(nodes, true) },
                { "witnessValue", witness.ReadValue() }, { "witnessCalls", witness.Calls },
                { "counter", owner == null ? null : (object)owner.Counter },
                { "observed", owner == null ? null : (object)owner.Observed },
                { "sameArray", owner == null ? null : (object)ReferenceEquals(owner.Nodes, nodes) },
                { "aliasedElements", nodes == null || nodes.Length < 3 ? null :
                    (object)ReferenceEquals(nodes[0], nodes[2]) }
            });
        }

        private static int[] Snapshot(int[] values) { return values == null ? null : (int[])values.Clone(); }

        private static object[] NodeValues(ArrayNode[] nodes, bool calls)
        {
            if (nodes == null) return null;
            var values = new object[nodes.Length];
            for (var index = 0; index < nodes.Length; index++)
                values[index] = nodes[index] == null ? null : (object)(calls ? nodes[index].Calls : nodes[index].Value);
            return values;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report") continue;
                try { Write(arguments[index + 1], "player"); Application.Quit(0); }
                catch (Exception error) { Debug.LogException(error); Application.Quit(1); }
                return;
            }
        }
    }
}
