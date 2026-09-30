using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeReferenceFieldInvocationFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly string[] Cases =
        {
            "success", "target-null", "source-null", "holder-null", "payload-alias",
            "counter-negative", "counter-overflow"
        };

        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
            var methods = 0;
            var fields = 0;
            var signatures = new Dictionary<string, object>();
            foreach (var type in new[] { typeof(Payload), typeof(Node), typeof(InvocationHolder) })
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
            var firstPayload = new Payload();
            var secondPayload = new Payload();
            var observations = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" }, { "methods", methods }, { "fields", fields },
                    { "targetType", typeof(InvocationHolder).GetField("Target").FieldType.FullName },
                    { "sourceType", typeof(InvocationHolder).GetField("Source").FieldType.FullName },
                    { "valueType", typeof(Node).GetField("Value").FieldType.FullName }, { "signatures", signatures }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" },
                    { "holder", HolderState(new InvocationHolder(), null, null, firstPayload, secondPayload) },
                    { "node", NodeState(new Node(), firstPayload, secondPayload) }
                }
            };
            foreach (var flag in new[] { false, true })
                foreach (var kind in Cases)
                {
                    var first = new Node { Calls = 7, Value = kind == "payload-alias" ? firstPayload : secondPayload };
                    var second = new Node { Calls = -13, Value = firstPayload };
                    if (kind == "counter-negative") first.Calls = int.MinValue;
                    if (kind == "counter-overflow") first.Calls = int.MaxValue;
                    var owner = kind == "holder-null" ? null : new InvocationHolder
                    {
                        Target = kind == "target-null" ? null : first,
                        Source = kind == "source-null" ? null : firstPayload,
                        Flag = flag, Marker = -2.0f
                    };
                    Record(observations, kind, flag, owner, first, second, firstPayload, secondPayload);
                }

            var reusedFirst = new Node { Calls = 17, Value = secondPayload };
            var reusedSecond = new Node { Calls = -13, Value = firstPayload };
            var reusedOwner = new InvocationHolder
            {
                Source = firstPayload, Marker = BitConverter.ToSingle(BitConverter.GetBytes(int.MinValue), 0)
            };
            Record(observations, "reuse-target-null", false, reusedOwner, reusedFirst, reusedSecond, firstPayload, secondPayload);
            reusedOwner.Target = reusedFirst;
            reusedOwner.Source = null;
            Record(observations, "reuse-source-null", false, reusedOwner, reusedFirst, reusedSecond, firstPayload, secondPayload);
            reusedOwner.Source = firstPayload;
            Record(observations, "reuse-first", false, reusedOwner, reusedFirst, reusedSecond, firstPayload, secondPayload);
            reusedOwner.Target = reusedSecond;
            reusedOwner.Source = secondPayload;
            Record(observations, "reuse-second", false, reusedOwner, reusedFirst, reusedSecond, firstPayload, secondPayload);
            Record(observations, "repeat-second", false, reusedOwner, reusedFirst, reusedSecond, firstPayload, secondPayload);
            reusedOwner.Target = reusedFirst;
            reusedOwner.Source = null;
            Record(observations, "replace-source-null", false, reusedOwner, reusedFirst, reusedSecond, firstPayload, secondPayload);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-reference-field-invocation" }, { "observations", observations }
            }));
        }

        private static string PayloadIdentity(Payload value, Payload first, Payload second)
        {
            if (value == null) return "null";
            if (ReferenceEquals(value, first)) return "first";
            return ReferenceEquals(value, second) ? "second" : "other";
        }

        private static object NodeState(Node node, Payload first, Payload second)
        {
            return new Dictionary<string, object>
            {
                { "calls", node.Calls }, { "value", PayloadIdentity(node.Value, first, second) }
            };
        }

        private static object HolderState(InvocationHolder owner, Node first, Node second, Payload firstPayload, Payload secondPayload)
        {
            if (owner == null) return null;
            var target = owner.Target == null ? "null" : ReferenceEquals(owner.Target, first) ? "first" :
                ReferenceEquals(owner.Target, second) ? "second" : "other";
            return new Dictionary<string, object>
            {
                { "target", target }, { "source", PayloadIdentity(owner.Source, firstPayload, secondPayload) },
                { "flag", owner.Flag }, { "markerBits", BitConverter.ToUInt32(BitConverter.GetBytes(owner.Marker), 0).ToString("x8") }
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(InvocationHolder owner) => owner.Forward();

        private static void Record(List<object> rows, string kind, bool initialFlag, InvocationHolder owner,
            Node first, Node second, Payload firstPayload, Payload secondPayload)
        {
            var before = HolderState(owner, first, second, firstPayload, secondPayload);
            var exception = "none";
            try { Invoke(owner); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "initialFlag", initialFlag }, { "exception", exception },
                { "before", before }, { "holder", HolderState(owner, first, second, firstPayload, secondPayload) },
                { "first", NodeState(first, firstPayload, secondPayload) },
                { "second", NodeState(second, firstPayload, secondPayload) }
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
