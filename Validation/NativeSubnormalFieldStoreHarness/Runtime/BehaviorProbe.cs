using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeSubnormalFieldStoreFixture;
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
                    { "singleType", holderType.GetField("SingleMarker").FieldType.FullName },
                    { "doubleType", holderType.GetField("DoubleMarker").FieldType.FullName },
                    { "parameterType", nodeType.GetMethod("Set").GetParameters()[0].ParameterType.FullName }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "holder", HolderState(new InvocationHolder(), null, null) },
                    { "node", State(new InvocationNode()) }
                }
            };
            for (var operation = 0; operation < 8; operation++)
            {
                Scenario(rows, operation, "success", false, false, false, true);
                Scenario(rows, operation, "target-null", true, false, false, false);
                Scenario(rows, operation, "holder-null", false, true, false, true);
                Scenario(rows, operation, "alias", false, false, true, false);
                Scenario(rows, operation, "false-success", false, false, false, false);
                var first = new InvocationNode { Calls = 7, Value = false };
                var second = new InvocationNode { Calls = -13, Value = true };
                var owner = new InvocationHolder { SingleMarker = 1.0F, DoubleMarker = 1.0D };
                Record(rows, operation, "reuse-failure", owner, first, second, true);
                owner.Target = first;
                Record(rows, operation, "reuse-success", owner, first, second, false);
                Record(rows, operation, "repeat", owner, first, second, true);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-subnormal-field-store" }, { "observations", rows }
            }));
        }

        private static object State(InvocationNode node)
        {
            return new Dictionary<string, object> { { "calls", node.Calls }, { "value", node.Value } };
        }

        private static object HolderState(InvocationHolder owner, InvocationNode first, InvocationNode second)
        {
            if (owner == null) return null;
            return new Dictionary<string, object>
            {
                { "singleBits", BitConverter.ToInt32(BitConverter.GetBytes(owner.SingleMarker), 0) },
                { "doubleBits", BitConverter.DoubleToInt64Bits(owner.DoubleMarker) },
                { "targetNull", owner.Target == null },
                { "targetFirst", owner.Target != null && ReferenceEquals(owner.Target, first) },
                { "targetSecond", owner.Target != null && ReferenceEquals(owner.Target, second) }
            };
        }

        private static void Scenario(List<object> rows, int operation, string kind, bool targetNull,
            bool holderNull, bool alias, bool value)
        {
            var first = new InvocationNode { Calls = 7, Value = !value };
            var second = alias ? first : new InvocationNode { Calls = -13, Value = value };
            var owner = holderNull ? null : new InvocationHolder
            {
                Target = targetNull ? null : first, SingleMarker = 1.0F, DoubleMarker = 1.0D
            };
            Record(rows, operation, kind, owner, first, second, value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(InvocationHolder owner, int operation, bool value)
        {
            switch (operation)
            {
                case 0: owner.StoreSingleMinimum(value); return;
                case 1: owner.StoreSingleMaximum(value); return;
                case 2: owner.StoreSingleNegativeMinimum(value); return;
                case 3: owner.StoreSingleNegativeMaximum(value); return;
                case 4: owner.StoreDoubleMinimum(value); return;
                case 5: owner.StoreDoubleMaximumAndSingleMinimum(value); return;
                case 6: owner.StoreDoubleNegativeMinimum(value); return;
                case 7: owner.StoreDoubleNegativeMaximum(value); return;
                default: throw new ArgumentOutOfRangeException("operation");
            }
        }

        private static void Record(List<object> rows, int operation, string kind, InvocationHolder owner,
            InvocationNode first, InvocationNode second, bool value)
        {
            var exception = "none";
            try { Invoke(owner, operation, value); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", operation }, { "value", value }, { "exception", exception },
                { "holder", HolderState(owner, first, second) }, { "alias", ReferenceEquals(first, second) },
                { "first", State(first) }, { "second", State(second) }
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
