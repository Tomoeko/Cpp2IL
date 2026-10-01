using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using TypedFieldAddressDiscardedResultFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly long[] Samples =
        {
            0, 1, -1, 2, -2, long.MaxValue, long.MinValue,
            long.MaxValue - 1, long.MinValue + 1, int.MaxValue, int.MinValue, 1099511627776
        };
        private static readonly int[] Modes = { 0, 1, -1, int.MaxValue, int.MinValue };
        private static readonly string[] Methods =
        {
            "ResetFirstDiscard", "ResetBothDiscard", "ApplyDefault", "GuardedApplyDefault"
        };

        public static void Write(string path, string stage)
        {
            var rows = new List<object>();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var signatures = new Dictionary<string, object>();
            foreach (var type in new[] { typeof(ResultCell), typeof(AddressResultOwner) })
                foreach (var method in type.GetMethods(flags))
                {
                    var signature = new List<string> { method.ReturnType.FullName };
                    foreach (var parameter in method.GetParameters()) signature.Add(parameter.ParameterType.FullName);
                    signatures.Add(type.Name + "." + method.Name, signature);
                }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "declarations" }, { "methods", signatures.Count + typeof(AddressResultOwner).GetConstructors(flags).Length },
                { "fields", typeof(ResultCell).GetFields(flags).Length + typeof(AddressResultOwner).GetFields(flags).Length +
                    typeof(ResultCode).GetFields(flags).Length + typeof(UpdateMode).GetFields(flags).Length },
                { "cellSize", Marshal.SizeOf(typeof(ResultCell)) }, { "cellSequential", typeof(ResultCell).IsLayoutSequential },
                { "cellValueType", typeof(ResultCell).GetField("Value").FieldType.FullName },
                { "firstType", typeof(AddressResultOwner).GetField("First").FieldType.FullName },
                { "secondType", typeof(AddressResultOwner).GetField("Second").FieldType.FullName },
                { "enabledType", typeof(AddressResultOwner).GetField("Enabled").FieldType.FullName },
                { "resultUnderlying", Enum.GetUnderlyingType(typeof(ResultCode)).FullName },
                { "modeUnderlying", Enum.GetUnderlyingType(typeof(UpdateMode)).FullName },
                { "resultIdle", (int)ResultCode.Idle }, { "resultChanged", (int)ResultCode.Changed },
                { "modeDefault", (int)UpdateMode.Default }, { "modeAlternative", (int)UpdateMode.Alternative },
                { "signatures", signatures }
            });
            var defaults = State(new AddressResultOwner());
            defaults.Add("kind", "defaults"); defaults.Add("resultDefault", (int)default(ResultCode));
            defaults.Add("modeDefault", (int)default(UpdateMode)); rows.Add(defaults);
            foreach (var input in Samples)
            {
                foreach (var method in Methods)
                {
                    if (method == "GuardedApplyDefault") Record(rows, "operation", method, NewOwner(input), false);
                    Record(rows, "operation", method, NewOwner(input), true);
                }
                RecordCell(rows, "ResetAndReport", input, 0);
                foreach (var mode in Modes) RecordCell(rows, "IncrementAndReport", input, mode);
            }
            foreach (var method in Methods)
            {
                var sentinel = NewOwner(19);
                var exception = "none";
                try { Invoke(method, null); }
                catch (Exception error) { exception = error.GetType().FullName; }
                var row = State(sentinel);
                row.Add("kind", "null-owner"); row.Add("method", method); row.Add("exception", exception); rows.Add(row);
            }
            var reused = NewOwner(19);
            Record(rows, "reuse", "ApplyDefault", reused, true);
            Record(rows, "reuse", "GuardedApplyDefault", reused, false);
            Record(rows, "reuse", "GuardedApplyDefault", reused, true);
            Record(rows, "reuse", "ResetFirstDiscard", reused, true);
            Record(rows, "reuse", "ApplyDefault", reused, true);
            Record(rows, "reuse", "ResetBothDiscard", reused, true);
            RecordAlias(rows, "ApplyDefault", long.MaxValue);
            RecordAlias(rows, "ResetBothDiscard", -37);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "profile", "typed-field-address-discarded-result" }, { "stage", stage },
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "observations", rows }
            }));
        }

        private static AddressResultOwner NewOwner(long value)
        {
            return new AddressResultOwner
            {
                First = new ResultCell { Value = value }, Second = new ResultCell { Value = -23 }, Enabled = true
            };
        }

        private static Dictionary<string, object> State(AddressResultOwner owner)
        {
            return new Dictionary<string, object>
            {
                { "first", owner.First.Value }, { "second", owner.Second.Value }, { "enabled", owner.Enabled }
            };
        }

        private static void Invoke(string method, AddressResultOwner owner)
        {
            switch (method)
            {
                case "ResetFirstDiscard": owner.ResetFirstDiscard(); break;
                case "ResetBothDiscard": owner.ResetBothDiscard(); break;
                case "ApplyDefault": owner.ApplyDefault(); break;
                case "GuardedApplyDefault": owner.GuardedApplyDefault(); break;
                default: throw new InvalidOperationException("Unknown fixture method");
            }
        }

        private static void Record(List<object> rows, string kind, string method, AddressResultOwner owner, bool enabled)
        {
            owner.Enabled = enabled;
            var copy = owner.First;
            var owners = new[] { owner, NewOwner(19) };
            var alias = owners;
            Invoke(method, alias[0]);
            var row = State(owner);
            row.Add("kind", kind); row.Add("method", method); row.Add("input", copy.Value);
            row.Add("exception", "none"); row.Add("copyFirst", copy.Value);
            row.Add("sameOwner", ReferenceEquals(owner, alias[0])); row.Add("sameArray", ReferenceEquals(owners, alias));
            row.Add("neighbor", State(alias[1])); rows.Add(row);
        }

        private static void RecordCell(List<object> rows, string method, long input, int mode)
        {
            var cell = new ResultCell { Value = input };
            var copy = cell;
            var result = method == "ResetAndReport" ? cell.ResetAndReport() : cell.IncrementAndReport((UpdateMode)mode);
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "callee" }, { "method", method }, { "input", input }, { "mode", mode },
                { "result", (int)result }, { "value", cell.Value }, { "copyValue", copy.Value }, { "exception", "none" }
            });
        }

        private static void RecordAlias(List<object> rows, string method, long assigned)
        {
            var owner = NewOwner(19);
            var copy = owner.First;
            var owners = new[] { owner, NewOwner(19) };
            var alias = owners;
            ref ResultCell slot = ref alias[0].First;
            slot.Value = assigned;
            Invoke(method, alias[0]);
            var row = State(owner);
            row.Add("kind", "ref-alias"); row.Add("method", method); row.Add("assigned", assigned);
            row.Add("slotAfter", slot.Value); row.Add("copyFirst", copy.Value);
            row.Add("sameOwner", ReferenceEquals(owner, alias[0])); row.Add("sameArray", ReferenceEquals(owners, alias));
            row.Add("neighbor", State(alias[1])); rows.Add(row);
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
