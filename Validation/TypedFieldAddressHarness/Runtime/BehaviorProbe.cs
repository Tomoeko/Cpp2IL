using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using TypedFieldAddressFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly long[] Samples =
        {
            0, 1, -1, 2, -2, long.MaxValue, long.MinValue,
            long.MaxValue - 1, long.MinValue + 1, int.MaxValue, int.MinValue,
            1099511627776, -1099511627776, 1311768467463790320
        };
        private static readonly int[] SmallSamples =
        {
            0, 1, -1, int.MaxValue, int.MinValue, int.MaxValue - 1, int.MinValue + 1,
            32767, -32768, 65535, -65536
        };
        private static readonly string[] LongMethods =
        {
            "AdvanceFirst", "AdvanceSecond", "ReadFirst", "AdvanceTwice", "GuardedAdvance"
        };
        private static readonly string[] NullMethods =
        {
            "AdvanceFirst", "AdvanceSecond", "AdvanceSmall", "ResetFirst", "ReadFirst",
            "GuardedAdvance", "AdvanceTwice"
        };

        public static void Write(string path, string stage)
        {
            var rows = new List<object>();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var signatures = new Dictionary<string, object>();
            foreach (var type in new[] { typeof(CounterCell), typeof(SmallCell), typeof(StorageOwner) })
                foreach (var method in type.GetMethods(flags))
                {
                    var signature = new List<string> { method.ReturnType.FullName };
                    foreach (var parameter in method.GetParameters())
                        signature.Add(parameter.ParameterType.FullName);
                    signatures.Add(type.Name + "." + method.Name, signature);
                }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "declarations" }, { "methods", signatures.Count + typeof(StorageOwner).GetConstructors(flags).Length },
                { "fields", typeof(CounterCell).GetFields(flags).Length + typeof(SmallCell).GetFields(flags).Length + typeof(StorageOwner).GetFields(flags).Length },
                { "counterSize", Marshal.SizeOf(typeof(CounterCell)) }, { "smallSize", Marshal.SizeOf(typeof(SmallCell)) },
                { "counterSequential", typeof(CounterCell).IsLayoutSequential }, { "smallSequential", typeof(SmallCell).IsLayoutSequential },
                { "counterValueType", typeof(CounterCell).GetField("Value").FieldType.FullName },
                { "smallValueType", typeof(SmallCell).GetField("Value").FieldType.FullName },
                { "firstType", typeof(StorageOwner).GetField("First").FieldType.FullName },
                { "secondType", typeof(StorageOwner).GetField("Second").FieldType.FullName },
                { "smallType", typeof(StorageOwner).GetField("Small").FieldType.FullName },
                { "enabledType", typeof(StorageOwner).GetField("Enabled").FieldType.FullName }, { "signatures", signatures }
            });
            var defaults = State(new StorageOwner());
            defaults.Add("kind", "defaults");
            rows.Add(defaults);
            foreach (var input in Samples)
                foreach (var method in LongMethods)
                {
                    if (method == "GuardedAdvance")
                        Record(rows, "operation", method, NewOwner(), input, false);
                    Record(rows, "operation", method, NewOwner(), input, true);
                }
            Record(rows, "operation", "ResetFirst", NewOwner(), 0, true);
            foreach (var input in SmallSamples)
                Record(rows, "operation", "AdvanceSmall", NewOwner(), input, true);
            foreach (var method in NullMethods)
            {
                var sentinel = NewOwner();
                var exception = "none";
                try { Invoke(method, null, 17); }
                catch (Exception error) { exception = error.GetType().FullName; }
                var row = State(sentinel);
                row.Add("kind", "null-owner"); row.Add("method", method); row.Add("exception", exception);
                rows.Add(row);
            }
            var reused = NewOwner();
            Record(rows, "reuse", "AdvanceFirst", reused, long.MaxValue, true);
            Record(rows, "reuse", "AdvanceSecond", reused, -17, true);
            Record(rows, "reuse", "AdvanceSmall", reused, int.MaxValue, true);
            Record(rows, "reuse", "ReadFirst", reused, 0, true);
            Record(rows, "reuse", "GuardedAdvance", reused, 3, false);
            Record(rows, "reuse", "GuardedAdvance", reused, 3, true);
            Record(rows, "reuse", "AdvanceTwice", reused, -5, true);
            Record(rows, "reuse", "ResetFirst", reused, 0, true);
            RecordAlias(rows, "AdvanceFirst", 36, 3);
            RecordAlias(rows, "AdvanceTwice", 41, -5);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "profile", "typed-field-address" }, { "stage", stage }, { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() }, { "observations", rows }
            }));
        }

        private static StorageOwner NewOwner()
        {
            return new StorageOwner
            {
                First = new CounterCell { Value = 19 }, Second = new CounterCell { Value = -23 },
                Small = new SmallCell { Value = 31 }, Enabled = true
            };
        }

        private static Dictionary<string, object> State(StorageOwner owner)
        {
            return new Dictionary<string, object>
            {
                { "first", owner.First.Value }, { "second", owner.Second.Value },
                { "small", owner.Small.Value }, { "enabled", owner.Enabled }
            };
        }

        private static long? Invoke(string method, StorageOwner owner, long input)
        {
            switch (method)
            {
                case "AdvanceFirst": return owner.AdvanceFirst(input);
                case "AdvanceSecond": return owner.AdvanceSecond(input);
                case "AdvanceSmall": return owner.AdvanceSmall(unchecked((int)input));
                case "ResetFirst": owner.ResetFirst(); return null;
                case "ReadFirst": return owner.ReadFirst();
                case "GuardedAdvance": return owner.GuardedAdvance(input);
                case "AdvanceTwice": return owner.AdvanceTwice(input);
                default: throw new InvalidOperationException("Unknown fixture method");
            }
        }

        private static void Record(List<object> rows, string kind, string method, StorageOwner owner, long input, bool enabled)
        {
            owner.Enabled = enabled;
            var copy = owner.First;
            var owners = new[] { owner, NewOwner() };
            var alias = owners;
            long? result = null;
            var exception = "none";
            try { result = Invoke(method, alias[0], input); }
            catch (Exception error) { exception = error.GetType().FullName; }
            var row = State(owner);
            row.Add("kind", kind); row.Add("method", method); row.Add("input", input);
            row.Add("exception", exception); row.Add("result", result);
            row.Add("copyFirst", copy.Value); row.Add("sameOwner", ReferenceEquals(owner, alias[0]));
            row.Add("sameArray", ReferenceEquals(owners, alias)); row.Add("neighbor", State(alias[1]));
            rows.Add(row);
        }

        private static void RecordAlias(List<object> rows, string method, long assigned, long input)
        {
            var owner = NewOwner();
            var copy = owner.First;
            var owners = new[] { owner, NewOwner() };
            var alias = owners;
            ref CounterCell slot = ref alias[0].First;
            slot.Value = assigned;
            var result = Invoke(method, owner, input);
            var row = State(owner);
            row.Add("kind", "ref-alias"); row.Add("method", method); row.Add("assigned", assigned); row.Add("input", input);
            row.Add("result", result); row.Add("slotAfter", slot.Value); row.Add("copyFirst", copy.Value);
            row.Add("sameOwner", ReferenceEquals(owner, alias[0])); row.Add("sameArray", ReferenceEquals(owners, alias));
            row.Add("neighbor", State(alias[1]));
            rows.Add(row);
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
