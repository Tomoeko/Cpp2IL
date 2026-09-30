using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeBooleanToggleInvocationFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
            var nodeType = typeof(Node);
            var holderType = typeof(InvocationHolder);
            var rows = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" },
                    { "methods", nodeType.GetMethods(flags).Length + nodeType.GetConstructors(flags).Length +
                        holderType.GetMethods(flags).Length + holderType.GetConstructors(flags).Length },
                    { "fields", nodeType.GetFields(flags).Length + holderType.GetFields(flags).Length },
                    { "parameterType", nodeType.GetMethod("SetFlag").GetParameters()[0].ParameterType.FullName },
                    { "toggleParameters", holderType.GetMethod("ToggleAndForward").GetParameters().Length },
                    { "flagType", holderType.GetField("Flag").FieldType.FullName },
                    { "targetType", holderType.GetField("Target").FieldType.FullName }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "holder", HolderState(new InvocationHolder(), null) },
                    { "node", NodeState(new Node()) }
                }
            };
            foreach (var initial in new[] { false, true })
            {
                var node = NewNode(initial);
                Record(rows, "success", initial, new InvocationHolder { Flag = initial, Target = node }, null, node);
                node = NewNode(initial);
                Record(rows, "target-null", initial, new InvocationHolder { Flag = initial }, null, node);
                node = NewNode(initial);
                Record(rows, "holder-null", initial, null, null, node);
                node = NewNode(initial);
                var owner = new InvocationHolder { Flag = initial };
                Record(rows, "reuse-failure", initial, owner, null, node);
                owner.Target = node;
                Record(rows, "reuse-success", initial, owner, null, node);
                Record(rows, "repeat", initial, owner, null, node);
                node = NewNode(initial);
                var first = new InvocationHolder { Flag = initial, Target = node };
                var second = new InvocationHolder { Flag = !initial, Target = node };
                Record(rows, "shared-target-first", initial, first, second, node);
                Record(rows, "shared-target-second", initial, second, first, node);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-boolean-toggle-invocation" }, { "observations", rows }
            }));
        }

        private static Node NewNode(bool flag)
        {
            return new Node { Calls = 7, Flag = flag };
        }

        private static object NodeState(Node node)
        {
            return new Dictionary<string, object> { { "calls", node.Calls }, { "flag", node.Flag } };
        }

        private static object HolderState(InvocationHolder owner, Node node)
        {
            if (owner == null) return null;
            return new Dictionary<string, object>
            {
                { "flag", owner.Flag }, { "targetNull", owner.Target == null },
                { "targetNode", owner.Target != null && ReferenceEquals(owner.Target, node) }
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(InvocationHolder owner)
        {
            owner.ToggleAndForward();
        }

        private static void Record(List<object> rows, string kind, bool initial, InvocationHolder owner,
            InvocationHolder other, Node node)
        {
            var exception = "none";
            try { Invoke(owner); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "initialFlag", initial }, { "exception", exception },
                { "holder", HolderState(owner, node) }, { "otherHolder", HolderState(other, node) },
                { "sharedTarget", owner != null && other != null && owner.Target != null &&
                    ReferenceEquals(owner.Target, other.Target) }, { "node", NodeState(node) }
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
