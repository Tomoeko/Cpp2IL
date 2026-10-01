using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeConstantReferenceArrayArgumentFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly string[] DirectCases =
        {
            "direct-null-cell", "direct-first", "direct-second", "direct-null-payload",
            "direct-counter-negative", "direct-counter-overflow"
        };

        private static readonly string[] ForwardCases =
        {
            "cell-null", "array-null", "array-empty", "first-null", "first-first",
            "first-second", "element-alias", "value-alias", "counter-negative", "counter-overflow"
        };

        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var methods = 0;
            var fields = 0;
            var properties = 0;
            var signatures = new Dictionary<string, object>();
            var fieldTypes = new Dictionary<string, object>();
            var fieldAccess = new Dictionary<string, object>();
            var types = new[] { typeof(Payload), typeof(Cell) };
            foreach (var type in types)
            {
                methods += type.GetMethods(flags).Length + type.GetConstructors(flags).Length;
                fields += type.GetFields(flags).Length;
                properties += type.GetProperties(flags).Length;
                foreach (var method in type.GetMethods(flags))
                {
                    var signature = new List<string> { method.ReturnType.FullName };
                    foreach (var parameter in method.GetParameters()) signature.Add(parameter.ParameterType.FullName);
                    signatures.Add(type.Name + "." + method.Name, signature);
                }
                foreach (var field in type.GetFields(flags))
                {
                    fieldTypes.Add(type.Name + "." + field.Name, field.FieldType.FullName);
                    fieldAccess.Add(type.Name + "." + field.Name, (field.Attributes & FieldAttributes.FieldAccessMask).ToString());
                }
            }
            var firstPayload = new Payload();
            var secondPayload = new Payload();
            var empty = new Cell();
            var rows = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" }, { "methods", methods }, { "fields", fields },
                    { "properties", properties }, { "types", types.Length }, { "signatures", signatures },
                    { "fieldTypes", fieldTypes }, { "fieldAccess", fieldAccess }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "itemsNull", empty.Items == null },
                    { "calls", empty.Calls }, { "valueNull", empty.Value == null }
                }
            };
            foreach (var kind in DirectCases)
            {
                var firstArray = new[] { firstPayload, secondPayload };
                var secondArray = new[] { secondPayload, firstPayload };
                var first = new Cell { Items = firstArray, Calls = 7, Value = secondPayload };
                var second = new Cell { Items = secondArray, Calls = -13, Value = firstPayload };
                var owner = kind == "direct-null-cell" ? null : kind == "direct-second" ? second : first;
                var incoming = kind == "direct-null-payload" ? null : kind == "direct-second" ? secondPayload : firstPayload;
                if (kind == "direct-counter-negative") first.Calls = int.MinValue;
                if (kind == "direct-counter-overflow") first.Calls = int.MaxValue;
                Record(rows, kind, owner, first, second, firstArray, secondArray, firstPayload, secondPayload, true, incoming);
            }
            foreach (var kind in ForwardCases)
            {
                var firstArray = kind == "array-null" ? null : kind == "array-empty" ? new Payload[0] :
                    kind == "first-second" ? new[] { secondPayload, firstPayload } :
                    kind == "element-alias" ? new[] { firstPayload, firstPayload } :
                    new[] { kind == "first-null" ? null : firstPayload, secondPayload };
                var secondArray = new[] { secondPayload, firstPayload };
                var first = new Cell { Items = firstArray, Calls = 7, Value = kind == "value-alias" ? firstPayload : secondPayload };
                var second = new Cell { Items = secondArray, Calls = -13, Value = firstPayload };
                if (kind == "counter-negative") first.Calls = int.MinValue;
                if (kind == "counter-overflow") first.Calls = int.MaxValue;
                Record(rows, kind, kind == "cell-null" ? null : first, first, second,
                    firstArray, secondArray, firstPayload, secondPayload);
            }
            var oldArray = new[] { (Payload)null, secondPayload };
            var replacementArray = new[] { firstPayload, secondPayload };
            var reusedFirst = new Cell { Calls = 17, Value = secondPayload };
            var reusedSecond = new Cell { Items = replacementArray, Calls = -13, Value = firstPayload };
            Record(rows, "reuse-array-null", reusedFirst, reusedFirst, reusedSecond, oldArray, replacementArray, firstPayload, secondPayload);
            reusedFirst.Items = new Payload[0];
            Record(rows, "reuse-array-empty", reusedFirst, reusedFirst, reusedSecond, oldArray, replacementArray, firstPayload, secondPayload);
            reusedFirst.Items = oldArray;
            Record(rows, "reuse-first-null", reusedFirst, reusedFirst, reusedSecond, oldArray, replacementArray, firstPayload, secondPayload);
            oldArray[0] = firstPayload;
            Record(rows, "reuse-first", reusedFirst, reusedFirst, reusedSecond, oldArray, replacementArray, firstPayload, secondPayload);
            Record(rows, "repeat-first", reusedFirst, reusedFirst, reusedSecond, oldArray, replacementArray, firstPayload, secondPayload);
            oldArray[0] = secondPayload;
            Record(rows, "reuse-replaced-element", reusedFirst, reusedFirst, reusedSecond, oldArray, replacementArray, firstPayload, secondPayload);
            reusedFirst.Items = replacementArray;
            Record(rows, "reuse-replaced-array", reusedFirst, reusedFirst, reusedSecond, oldArray, replacementArray, firstPayload, secondPayload);
            Record(rows, "shared-array-second-cell", reusedSecond, reusedFirst, reusedSecond, oldArray, replacementArray, firstPayload, secondPayload);
            replacementArray[0] = null;
            Record(rows, "shared-array-null-first-cell", reusedFirst, reusedFirst, reusedSecond, oldArray, replacementArray, firstPayload, secondPayload);
            replacementArray[0] = secondPayload;
            Record(rows, "shared-array-replaced-second-cell", reusedSecond, reusedFirst, reusedSecond, oldArray, replacementArray, firstPayload, secondPayload);
            reusedFirst.Items = null;
            Record(rows, "reuse-cleared-array", reusedFirst, reusedFirst, reusedSecond, oldArray, replacementArray, firstPayload, secondPayload);
            Record(rows, "other-cell-retained-array", reusedSecond, reusedFirst, reusedSecond, oldArray, replacementArray, firstPayload, secondPayload);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-constant-reference-array-argument" }, { "observations", rows }
            }));
        }

        private static string Identity(object value, object first, object second)
        {
            if (value == null) return "null";
            if (ReferenceEquals(value, first)) return "first";
            return ReferenceEquals(value, second) ? "second" : "other";
        }

        private static object ArrayValues(Payload[] items, Payload first, Payload second)
        {
            if (items == null) return null;
            var values = new List<string>();
            foreach (var item in items) values.Add(Identity(item, first, second));
            return values;
        }

        private static object CellState(Cell cell, Payload[] firstArray, Payload[] secondArray, Payload first, Payload second)
        {
            return new Dictionary<string, object>
            {
                { "items", Identity(cell.Items, firstArray, secondArray) }, { "calls", cell.Calls },
                { "value", Identity(cell.Value, first, second) }, { "itemValues", ArrayValues(cell.Items, first, second) }
            };
        }

        private static object State(Cell firstCell, Cell secondCell, Payload[] firstArray, Payload[] secondArray, Payload first, Payload second)
        {
            return new Dictionary<string, object>
            {
                { "first", CellState(firstCell, firstArray, secondArray, first, second) },
                { "second", CellState(secondCell, firstArray, secondArray, first, second) },
                { "firstArray", ArrayValues(firstArray, first, second) }, { "secondArray", ArrayValues(secondArray, first, second) }
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(Cell owner) => owner.Forward();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InvokeCapture(Cell owner, Payload incoming) => owner.Capture(incoming);

        private static void Record(List<object> rows, string kind, Cell owner, Cell first, Cell second,
            Payload[] firstArray, Payload[] secondArray, Payload firstPayload, Payload secondPayload,
            bool direct = false, Payload incoming = null)
        {
            var before = State(first, second, firstArray, secondArray, firstPayload, secondPayload);
            var exception = "none";
            try
            {
                if (direct) InvokeCapture(owner, incoming);
                else Invoke(owner);
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", direct ? "capture" : "forward" },
                { "receiver", Identity(owner, first, second) },
                { "argument", direct ? Identity(incoming, firstPayload, secondPayload) : "not-used" },
                { "exception", exception }, { "before", before },
                { "after", State(first, second, firstArray, secondArray, firstPayload, secondPayload) }
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
