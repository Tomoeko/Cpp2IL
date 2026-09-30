using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeScalarProducerInvocationFixture;
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
            var signatures = new Dictionary<string, object>();
            foreach (var type in new[] { nodeType, holderType })
                foreach (var method in type.GetMethods(flags))
                {
                    var signature = new List<string> { method.ReturnType.FullName };
                    foreach (var parameter in method.GetParameters()) signature.Add(parameter.ParameterType.FullName);
                    signatures.Add(type.Name + "." + method.Name, signature);
                }
            var observations = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" },
                    { "methods", nodeType.GetMethods(flags).Length + nodeType.GetConstructors(flags).Length +
                        holderType.GetMethods(flags).Length + holderType.GetConstructors(flags).Length },
                    { "fields", nodeType.GetFields(flags).Length + holderType.GetFields(flags).Length },
                    { "ownerType", nodeType.GetField("Owner").FieldType.FullName },
                    { "sourceType", holderType.GetField("Source").FieldType.FullName },
                    { "targetType", holderType.GetField("Target").FieldType.FullName },
                    { "replacementType", holderType.GetField("Replacement").FieldType.FullName },
                    { "signatures", signatures }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "holder", HolderState(new InvocationHolder(), null, null, null) },
                    { "node", NodeState(new Node(), null) }
                }
            };
            for (var operation = 0; operation < 2; operation++)
            {
                Scenario(observations, operation, "success", 29);
                Scenario(observations, operation, "source-null", -41);
                Scenario(observations, operation, "target-null", 47);
                Scenario(observations, operation, "holder-null", -53);
                Scenario(observations, operation, "source-target-alias", int.MinValue);
                Scenario(observations, operation, "target-replacement-alias", int.MaxValue);
                Scenario(observations, operation, "source-replacement-alias", 17);
                Scenario(observations, operation, "replacement-null", -19);
                Scenario(observations, operation, "source-owner-null", 23);
                Scenario(observations, operation, "minimum", int.MinValue);
                Scenario(observations, operation, "maximum", int.MaxValue);
                Scenario(observations, operation, "counter-overflow", -31);

                var source = NewNode(7, 11, int.MaxValue);
                var target = NewNode(-13, 17, -19);
                var replacement = NewNode(23, -29, 31);
                var owner = NewHolder(null, target, replacement);
                source.Owner = owner;
                Record(observations, operation, "reuse-source-null", owner, source, target, replacement);
                owner.Source = source;
                owner.Target = null;
                Record(observations, operation, "reuse-target-null", owner, source, target, replacement);
                owner.Target = target;
                source.Value = int.MinValue;
                Record(observations, operation, "reuse-success", owner, source, target, replacement);
                source.Value = int.MaxValue;
                Record(observations, operation, "repeat", owner, source, target, replacement);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-scalar-producer-invocation" }, { "observations", observations }
            }));
        }

        private static Node NewNode(int reads, int calls, int value)
        {
            return new Node { Reads = reads, Calls = calls, Value = value };
        }

        private static InvocationHolder NewHolder(Node source, Node target, Node replacement)
        {
            return new InvocationHolder
            {
                Source = source, Target = target, Replacement = replacement,
                Value = int.MaxValue, Neighbor = 43, Flag = false
            };
        }

        private static string Identity(Node node, Node source, Node target, Node replacement)
        {
            if (node == null) return "null";
            if (ReferenceEquals(node, source)) return "source";
            if (ReferenceEquals(node, target)) return "target";
            if (ReferenceEquals(node, replacement)) return "replacement";
            return "other";
        }

        private static object HolderState(InvocationHolder owner, Node source, Node target, Node replacement)
        {
            if (owner == null) return null;
            return new Dictionary<string, object>
            {
                { "source", Identity(owner.Source, source, target, replacement) },
                { "target", Identity(owner.Target, source, target, replacement) },
                { "replacement", Identity(owner.Replacement, source, target, replacement) },
                { "value", owner.Value }, { "neighbor", owner.Neighbor }, { "flag", owner.Flag },
                { "beforeCount", owner.BeforeCount }
            };
        }

        private static object NodeState(Node node, InvocationHolder owner)
        {
            return new Dictionary<string, object>
            {
                { "reads", node.Reads }, { "calls", node.Calls }, { "value", node.Value },
                { "ownerNull", node.Owner == null },
                { "ownerIsHolder", owner != null && ReferenceEquals(node.Owner, owner) }
            };
        }

        private static void Scenario(List<object> rows, int operation, string kind, int value)
        {
            var source = NewNode(7, 11, value);
            var target = kind == "source-target-alias" ? source : NewNode(-13, 17, -19);
            var replacement = kind == "target-replacement-alias" ? target :
                kind == "source-replacement-alias" ? source : NewNode(23, -29, 31);
            var owner = kind == "holder-null" ? null : NewHolder(
                kind == "source-null" ? null : source,
                kind == "target-null" ? null : target,
                kind == "replacement-null" ? null : replacement);
            source.Owner = kind == "source-owner-null" ? null : owner;
            if (kind == "counter-overflow")
            {
                source.Reads = int.MaxValue;
                target.Calls = int.MaxValue;
                owner.BeforeCount = int.MaxValue;
            }
            Record(rows, operation, kind, owner, source, target, replacement);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(InvocationHolder owner, int operation)
        {
            switch (operation)
            {
                case 0: owner.Forward(); return;
                case 1: owner.ForwardSnapshot(); return;
                default: throw new ArgumentOutOfRangeException("operation");
            }
        }

        private static void Record(List<object> rows, int operation, string kind, InvocationHolder owner,
            Node source, Node target, Node replacement)
        {
            var value = source.Value;
            var exception = "none";
            try { Invoke(owner, operation); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", operation }, { "producerValue", value }, { "exception", exception },
                { "holder", HolderState(owner, source, target, replacement) },
                { "sourceTargetAlias", ReferenceEquals(source, target) },
                { "sourceReplacementAlias", ReferenceEquals(source, replacement) },
                { "targetReplacementAlias", ReferenceEquals(target, replacement) },
                { "source", NodeState(source, owner) }, { "target", NodeState(target, owner) },
                { "replacement", NodeState(replacement, owner) }
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
