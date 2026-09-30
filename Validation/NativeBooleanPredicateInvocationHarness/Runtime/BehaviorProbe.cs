using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeBooleanPredicateInvocationFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
            var type = typeof(Node);
            var rows = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" },
                    { "methods", type.GetMethods(flags).Length + type.GetConstructors(flags).Length },
                    { "fields", type.GetFields(flags).Length },
                    { "parameterType", type.GetMethod("SetFlag").GetParameters()[0].ParameterType.FullName },
                    { "storedParameterType", type.GetMethod("ForwardStoredPair").GetParameters()[0].ParameterType.FullName },
                    { "negatedParameters", type.GetMethod("ForwardNegated").GetParameters().Length },
                    { "liveParameters", type.GetMethod("ForwardLivePair").GetParameters().Length },
                    { "complementParameters", type.GetMethod("ForwardComplementThenLive").GetParameters().Length },
                    { "flagType", type.GetField("Flag").FieldType.FullName },
                    { "firstType", type.GetField("First").FieldType.FullName },
                    { "secondType", type.GetField("Second").FieldType.FullName }
                },
                new Dictionary<string, object> { { "kind", "defaults" }, { "node", State(new Node(), null, null, null) } }
            };
            foreach (var operation in new[] { "negated", "stored-pair", "live-pair", "complement-live" })
                foreach (var initial in new[] { false, true })
                {
                    foreach (var kind in new[] { "success", "first-null", "second-null", "owner-null",
                        "self-first", "shared-target", "self-second" })
                    {
                        var first = new Node { Calls = 7, Flag = !initial };
                        var second = new Node { Calls = 11, Flag = initial };
                        var owner = new Node { Calls = 3, Flag = initial, First = first, Second = second };
                        if (kind == "first-null") owner.First = null;
                        if (kind == "second-null") owner.Second = null;
                        if (kind == "self-first") owner.First = owner;
                        if (kind == "shared-target") owner.Second = first;
                        if (kind == "self-second") owner.Second = owner;
                        if (kind == "owner-null") owner = null;
                        Record(rows, operation, kind, initial, !initial, owner, first, second);
                    }
                    var reusedFirst = new Node { Calls = 7, Flag = !initial };
                    var reusedSecond = new Node { Calls = 11, Flag = initial };
                    var reusedOwner = new Node { Calls = 3, Flag = initial, Second = reusedSecond };
                    Record(rows, operation, "reuse-failure", initial, !initial, reusedOwner, reusedFirst, reusedSecond);
                    reusedOwner.First = reusedFirst;
                    Record(rows, operation, "reuse-success", initial, !initial, reusedOwner, reusedFirst, reusedSecond);
                    Record(rows, operation, "repeat", initial, !initial, reusedOwner, reusedFirst, reusedSecond);
                }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-boolean-predicate-invocation" }, { "observations", rows }
            }));
        }

        private static object Identity(Node value, Node owner, Node first, Node second)
        {
            if (value == null) return null;
            if (ReferenceEquals(value, owner)) return "owner";
            if (ReferenceEquals(value, first)) return "first";
            if (ReferenceEquals(value, second)) return "second";
            return "unrecognized";
        }

        private static object State(Node node, Node owner, Node first, Node second)
        {
            if (node == null) return null;
            return new Dictionary<string, object>
            {
                { "calls", node.Calls }, { "flag", node.Flag },
                { "first", Identity(node.First, owner, first, second) },
                { "second", Identity(node.Second, owner, first, second) }
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(string operation, Node owner, bool input)
        {
            switch (operation)
            {
                case "negated": owner.ForwardNegated(); break;
                case "stored-pair": owner.ForwardStoredPair(input); break;
                case "live-pair": owner.ForwardLivePair(); break;
                case "complement-live": owner.ForwardComplementThenLive(); break;
                default: throw new ArgumentException("Unknown operation");
            }
        }

        private static void Record(List<object> rows, string operation, string kind, bool initial, bool input,
            Node owner, Node first, Node second)
        {
            var exception = "none";
            try { Invoke(operation, owner, input); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", operation }, { "initialFlag", initial }, { "inputFlag", input },
                { "exception", exception }, { "owner", State(owner, owner, first, second) },
                { "first", State(first, owner, first, second) }, { "second", State(second, owner, first, second) }
            });
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
