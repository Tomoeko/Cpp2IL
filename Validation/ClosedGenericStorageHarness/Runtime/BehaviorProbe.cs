using System;
using System.Collections.Generic;
using System.IO;
using ClosedGenericStorageFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Values =
        {
            int.MinValue, int.MinValue + 1, -65536, -32769, -32768, -129, -128, -1,
            0, 1, 2, 127, 128, 255, 256, 32767, 32768, 65535, 65536,
            int.MaxValue - 1, int.MaxValue
        };
        private static readonly int[] IntItems = { int.MinValue, -65536, -1, 0, 1, 65535, int.MaxValue };
        private static readonly long[] LongItems =
        {
            long.MinValue, long.MinValue + 1, -4294967296, -1, 0, 1,
            int.MaxValue, 4294967296, long.MaxValue
        };

        public static void Write(string path, string stage)
        {
            var rows = new List<object>();
            foreach (var value in Values)
            {
                var first = new IntState(value);
                rows.Add(Constructor("i4", value, first.Value, first.Prefix.Item, first.Prefix.Stamp,
                    first.Neighbor, first.Reference));
                var second = new LongState(value);
                rows.Add(Constructor("i8", value, second.Value, second.Prefix.Item, second.Prefix.Stamp,
                    second.Neighbor, second.Reference));
            }
            foreach (var value in Values)
            foreach (var item in IntItems)
            foreach (var neighbor in new[] { -137, 0, 911 })
            foreach (var missingReference in new[] { false, true })
            {
                var state = new IntState(value)
                {
                    Prefix = new Tail<int> { Item = item, Stamp = Stamp(item) },
                    Neighbor = neighbor, Reference = missingReference ? null : new object()
                };
                var alias = state;
                var reference = state.Reference;
                rows.Add(Observe("i4", value, item, Stamp(item), neighbor, missingReference,
                    state.ReadValue, alias.ReadValue, alias.Clear, () => state.Value,
                    () => state.Prefix.Item, () => state.Prefix.Stamp, () => state.Neighbor,
                    () => ReferenceEquals(reference, state.Reference), ReferenceEquals(state, alias)));
            }
            foreach (var value in Values)
            foreach (var item in LongItems)
            foreach (var neighbor in new[] { -137, 0, 911 })
            foreach (var missingReference in new[] { false, true })
            {
                var state = new LongState(value)
                {
                    Prefix = new Tail<long> { Item = item, Stamp = Stamp(item) },
                    Neighbor = neighbor, Reference = missingReference ? null : new object()
                };
                var alias = state;
                var reference = state.Reference;
                rows.Add(Observe("i8", value, item, Stamp(item), neighbor, missingReference,
                    state.ReadValue, alias.ReadValue, alias.Clear, () => state.Value,
                    () => state.Prefix.Item, () => state.Prefix.Stamp, () => state.Neighbor,
                    () => ReferenceEquals(reference, state.Reference), ReferenceEquals(state, alias)));
            }
            IntState missingInt = null;
            LongState missingLong = null;
            rows.Add(Missing("i4", "read", () => missingInt.ReadValue()));
            rows.Add(Missing("i4", "clear", () => { missingInt.Clear(); return null; }));
            rows.Add(Missing("i8", "read", () => missingLong.ReadValue()));
            rows.Add(Missing("i8", "clear", () => { missingLong.Clear(); return null; }));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "closed-generic-storage" }, { "observations", rows }
            }));
        }

        private static int Stamp(long item) { return unchecked((int)(item ^ (item >> 32))); }

        private static object Constructor(string carrier, int value, int after, long item, int stamp,
            int neighbor, object reference)
        {
            return new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "carrier", carrier }, { "value", value }, { "valueAfter", after },
                { "item", item }, { "stamp", stamp }, { "neighbor", neighbor }, { "referenceNull", reference == null }
            };
        }

        private static object Observe(string carrier, int value, long item, int stamp, int neighbor,
            bool missingReference, Func<int> read, Func<int> aliasRead, Action clear, Func<int> after,
            Func<long> itemAfter, Func<int> stampAfter, Func<int> neighborAfter, Func<bool> referenceSame, bool aliasSame)
        {
            int? first = null;
            int? repeat = null;
            int? clearedRead = null;
            var failure = "none";
            try { first = read(); repeat = aliasRead(); clear(); clearedRead = read(); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "state" }, { "carrier", carrier }, { "value", value }, { "item", item },
                { "stamp", stamp }, { "neighbor", neighbor }, { "referenceNull", missingReference },
                { "first", first }, { "repeat", repeat }, { "clearedRead", clearedRead }, { "failure", failure },
                { "valueAfter", after() }, { "itemAfter", itemAfter() }, { "stampAfter", stampAfter() },
                { "neighborAfter", neighborAfter() }, { "referenceSame", referenceSame() }, { "aliasSame", aliasSame }
            };
        }

        private static object Missing(string carrier, string operation, Func<int?> action)
        {
            int? value = null;
            var failure = "none";
            try { value = action(); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "null" }, { "carrier", carrier }, { "operation", operation },
                { "value", value }, { "failure", failure }
            };
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
