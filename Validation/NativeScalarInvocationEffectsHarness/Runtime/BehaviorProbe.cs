using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeScalarInvocationEffectsFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
            var nodeType = typeof(InvocationNode);
            var holderType = typeof(InvocationHolder);
            var rows = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" },
                    { "methods", nodeType.GetMethods(flags).Length + nodeType.GetConstructors(flags).Length +
                        holderType.GetMethods(flags).Length + holderType.GetConstructors(flags).Length },
                    { "fields", nodeType.GetFields(flags).Length + holderType.GetFields(flags).Length },
                    { "markerType", holderType.GetField("Marker").FieldType.FullName },
                    { "parameterType", nodeType.GetMethod("Set").GetParameters()[0].ParameterType.FullName },
                    { "flushParameters", nodeType.GetMethod("Flush").GetParameters().Length }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "markerBits", Bits(new InvocationHolder().Marker) },
                    { "node", State(new InvocationNode()) }
                }
            };
            for (var operation = 0; operation < 2; operation++)
                foreach (var value in new[] { false, true })
                    foreach (var kind in new[] { "target-null", "other-null", "distinct", "alias" })
                        Record(rows, operation, value, kind);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-scalar-invocation-effects" }, { "observations", rows }
            }));
        }

        private static int Bits(float value) { return BitConverter.ToInt32(BitConverter.GetBytes(value), 0); }

        private static object State(InvocationNode node)
        {
            return new Dictionary<string, object>
            {
                { "calls", node.Calls }, { "flushes", node.Flushes }, { "value", node.Value }
            };
        }

        private static void Record(List<object> rows, int operation, bool value, string kind)
        {
            var first = new InvocationNode { Calls = 7, Flushes = 11, Value = !value };
            var second = kind == "alias" ? first : new InvocationNode { Calls = -13, Flushes = 17, Value = value };
            var owner = new InvocationHolder
            {
                Target = kind == "target-null" ? null : first,
                Other = kind == "other-null" ? null : second,
                Marker = 1.0f
            };
            var exception = "none";
            try { Invoke(owner, operation, value); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", operation }, { "value", value }, { "exception", exception },
                { "markerBits", Bits(owner.Marker) }, { "alias", ReferenceEquals(first, second) },
                { "targetNull", owner.Target == null }, { "otherNull", owner.Other == null },
                { "first", State(first) }, { "second", State(second) }
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(InvocationHolder owner, int operation, bool value)
        {
            switch (operation)
            {
                case 0: owner.SetThenWrite(value); return;
                case 1: owner.SetWriteThenFlush(value); return;
                default: throw new ArgumentOutOfRangeException("operation");
            }
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
