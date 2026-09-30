using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeReferenceProducerInvocationFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly string[] Cases =
        {
            "success", "source-null", "result-null", "holder-null", "provider-alias", "node-alias",
            "replacement-null", "source-owner-null", "replacement-result-null", "replacement-owner-null",
            "negative-counters", "counter-overflow"
        };

        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
            var types = new[] { typeof(Node), typeof(Provider), typeof(InvocationHolder) };
            var methods = 0;
            var fields = 0;
            var signatures = new Dictionary<string, object>();
            foreach (var type in types)
            {
                methods += type.GetMethods(flags).Length + type.GetConstructors(flags).Length;
                fields += type.GetFields(flags).Length;
                foreach (var method in type.GetMethods(flags))
                {
                    var signature = new List<string> { method.ReturnType.FullName };
                    foreach (var parameter in method.GetParameters()) signature.Add(parameter.ParameterType.FullName);
                    signatures.Add(type.Name + "." + method.Name, signature);
                }
            }
            var observations = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" }, { "methods", methods }, { "fields", fields },
                    { "sourceType", typeof(InvocationHolder).GetField("Source").FieldType.FullName },
                    { "replacementType", typeof(InvocationHolder).GetField("Replacement").FieldType.FullName },
                    { "resultType", typeof(Provider).GetField("Result").FieldType.FullName },
                    { "ownerType", typeof(Provider).GetField("Owner").FieldType.FullName }, { "signatures", signatures }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "holder", HolderState(new InvocationHolder(), null, null) },
                    { "provider", ProviderState(new Provider(), null, null, null) }, { "node", NodeState(new Node()) }
                }
            };
            for (var operation = 0; operation < 2; operation++)
                for (var boolean = 0; boolean < 2; boolean++)
                {
                    var value = boolean != 0;
                    foreach (var kind in Cases) Scenario(observations, operation, kind, value);
                    var first = new Node { Calls = 7, Flag = !value };
                    var second = new Node { Calls = -13, Flag = value };
                    var source = new Provider { Reads = 7, Result = first };
                    var replacement = new Provider { Reads = -11, Result = second };
                    var owner = NewHolder(null, replacement);
                    source.Owner = replacement.Owner = owner;
                    Record(observations, operation, "reuse-source-null", value, owner, source, replacement, first, second);
                    owner.Source = source;
                    source.Result = null;
                    Record(observations, operation, "reuse-result-null", value, owner, source, replacement, first, second);
                    owner.Source = source;
                    source.Result = first;
                    Record(observations, operation, "reuse-success", value, owner, source, replacement, first, second);
                    Record(observations, operation, "repeat", !value, owner, source, replacement, first, second);
                }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-reference-producer-invocation" }, { "observations", observations }
            }));
        }

        private static InvocationHolder NewHolder(Provider source, Provider replacement)
        {
            return new InvocationHolder { Source = source, Replacement = replacement, Value = int.MaxValue, Neighbor = 43 };
        }

        private static string ProviderIdentity(Provider provider, Provider source, Provider replacement)
        {
            if (provider == null) return "null";
            if (ReferenceEquals(provider, source)) return "source";
            if (ReferenceEquals(provider, replacement)) return "replacement";
            return "other";
        }

        private static string NodeIdentity(Node node, Node first, Node second)
        {
            if (node == null) return "null";
            if (ReferenceEquals(node, first)) return "first";
            if (ReferenceEquals(node, second)) return "second";
            return "other";
        }

        private static object HolderState(InvocationHolder owner, Provider source, Provider replacement)
        {
            if (owner == null) return null;
            return new Dictionary<string, object>
            {
                { "source", ProviderIdentity(owner.Source, source, replacement) },
                { "replacement", ProviderIdentity(owner.Replacement, source, replacement) },
                { "value", owner.Value }, { "neighbor", owner.Neighbor }, { "flag", owner.Flag },
                { "beforeCount", owner.BeforeCount }
            };
        }

        private static object ProviderState(Provider provider, InvocationHolder owner, Node first, Node second)
        {
            return new Dictionary<string, object>
            {
                { "reads", provider.Reads }, { "result", NodeIdentity(provider.Result, first, second) },
                { "ownerNull", provider.Owner == null },
                { "ownerIsHolder", owner != null && ReferenceEquals(provider.Owner, owner) }
            };
        }

        private static object NodeState(Node node)
        {
            return new Dictionary<string, object> { { "calls", node.Calls }, { "flag", node.Flag } };
        }

        private static void Scenario(List<object> rows, int operation, string kind, bool value)
        {
            var first = new Node { Calls = 7, Flag = !value };
            var second = kind == "node-alias" ? first : new Node { Calls = -13, Flag = value };
            var source = new Provider { Reads = 7, Result = kind == "result-null" ? null : first };
            var replacement = kind == "provider-alias" ? source : new Provider
            {
                Reads = -11, Result = kind == "replacement-result-null" ? null : second
            };
            var owner = kind == "holder-null" ? null : NewHolder(
                kind == "source-null" ? null : source, kind == "replacement-null" ? null : replacement);
            source.Owner = replacement.Owner = owner;
            if (kind == "source-owner-null") source.Owner = null;
            if (kind == "replacement-owner-null") replacement.Owner = null;
            if (kind == "negative-counters" || kind == "counter-overflow")
            {
                var count = kind == "negative-counters" ? int.MinValue : int.MaxValue;
                source.Reads = first.Calls = owner.BeforeCount = count;
            }
            Record(rows, operation, kind, value, owner, source, replacement, first, second);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(InvocationHolder owner, int operation, bool value)
        {
            switch (operation)
            {
                case 0: owner.Forward(value); return;
                case 1: owner.ForwardSnapshot(value); return;
                default: throw new ArgumentOutOfRangeException("operation");
            }
        }

        private static void Record(List<object> rows, int operation, string kind, bool value,
            InvocationHolder owner, Provider source, Provider replacement, Node first, Node second)
        {
            var exception = "none";
            try { Invoke(owner, operation, value); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", operation }, { "value", value }, { "exception", exception },
                { "holder", HolderState(owner, source, replacement) },
                { "providerAlias", ReferenceEquals(source, replacement) }, { "nodeAlias", ReferenceEquals(first, second) },
                { "source", ProviderState(source, owner, first, second) },
                { "replacement", ProviderState(replacement, owner, first, second) },
                { "first", NodeState(first) }, { "second", NodeState(second) }
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
