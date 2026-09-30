using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ReferenceArraySearchFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly string[] Cases =
        {
            "holder-null", "null-array", "empty", "all-null", "distinct",
            "duplicate", "alias", "negative", "extremes", "tail-match"
        };
        private static readonly int[] Keys = { int.MinValue, -1, 0, 1, int.MaxValue };
        private static readonly Guid Tag = new Guid(17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59);

        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var methods = 0;
            var fields = 0;
            var signatures = new Dictionary<string, object>();
            foreach (var type in new[] { typeof(Entry), typeof(PaddedEntry), typeof(SearchHolder), typeof(PaddedSearchHolder) })
            {
                methods += type.GetMethods(flags).Length + type.GetConstructors(flags).Length;
                fields += type.GetFields(flags).Length;
                foreach (var method in type.GetMethods(flags))
                    signatures.Add(type.Name + "." + method.Name,
                        new[] { method.ReturnType.FullName, method.GetParameters()[0].ParameterType.FullName });
            }
            var rows = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" }, { "methods", methods }, { "fields", fields },
                    { "simpleArrayType", typeof(SearchHolder).GetField("Items").FieldType.FullName },
                    { "paddedArrayType", typeof(PaddedSearchHolder).GetField("Items").FieldType.FullName },
                    { "keyType", typeof(Entry).GetField("Key").FieldType.FullName },
                    { "paddedKeyType", typeof(PaddedEntry).GetField("Key").FieldType.FullName },
                    { "tagType", typeof(PaddedEntry).GetField("Tag").FieldType.FullName },
                    { "textType", typeof(PaddedEntry).GetField("Text").FieldType.FullName }, { "signatures", signatures }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "key", new Entry().Key }, { "paddedKey", new PaddedEntry().Key },
                    { "tag", new PaddedEntry().Tag.ToString() }, { "text", new PaddedEntry().Text },
                    { "simpleItemsNull", new SearchHolder().Items == null },
                    { "paddedItemsNull", new PaddedSearchHolder().Items == null },
                    { "neighbor", new PaddedSearchHolder().Neighbor }
                }
            };
            foreach (var padded in new[] { false, true })
                foreach (var operation in new[] { "find", "contains" })
                    foreach (var kind in Cases)
                        foreach (var key in Keys)
                            Record(rows, padded, operation, kind, key);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "reference-array-search" }, { "observations", rows }
            }));
        }

        private static int?[] Values(string kind)
        {
            switch (kind)
            {
                case "null-array": return null;
                case "empty": return new int?[0];
                case "all-null": return new int?[] { null, null };
                case "distinct": return new int?[] { 0, 1, -1 };
                case "duplicate": return new int?[] { 1, 1, 0 };
                case "alias": return new int?[] { 1, -1, 1 };
                case "negative": return new int?[] { -1, int.MinValue, 0 };
                case "extremes": return new int?[] { int.MinValue, int.MaxValue, 0 };
                case "tail-match": return new int?[] { null, 0, null, 1 };
                default: return new int?[] { 0, 1 };
            }
        }

        private static Array Items(bool padded, string kind)
        {
            var values = Values(kind);
            if (values == null) return null;
            var array = Array.CreateInstance(padded ? typeof(PaddedEntry) : typeof(Entry), values.Length);
            for (var index = 0; index < values.Length; index++)
            {
                if (values[index] == null) continue;
                object entry = padded
                    ? (object)new PaddedEntry { Key = values[index].Value, Tag = Tag, Text = "item-" + index }
                    : new Entry { Key = values[index].Value };
                array.SetValue(kind == "alias" && index == 2 ? array.GetValue(0) : entry, index);
            }
            return array;
        }

        private static object State(Array items, bool padded)
        {
            if (items == null) return null;
            var rows = new List<object>();
            for (var index = 0; index < items.Length; index++)
            {
                var entry = items.GetValue(index);
                if (entry == null) { rows.Add(null); continue; }
                var identity = 0;
                while (!ReferenceEquals(items.GetValue(identity), entry)) identity++;
                var row = new Dictionary<string, object>
                {
                    { "identity", identity },
                    { "key", padded ? ((PaddedEntry)entry).Key : ((Entry)entry).Key }
                };
                if (padded)
                {
                    row.Add("tag", ((PaddedEntry)entry).Tag.ToString());
                    row.Add("text", ((PaddedEntry)entry).Text);
                }
                rows.Add(row);
            }
            return rows;
        }

        private static void Record(List<object> rows, bool padded, string operation, string kind, int key)
        {
            var items = Items(padded, kind);
            var simple = new SearchHolder { Items = (Entry[])(padded ? null : items) };
            var wide = new PaddedSearchHolder { Items = (PaddedEntry[])(padded ? items : null), Neighbor = 43 };
            var before = State(items, padded);
            object result = null;
            object repeated = null;
            var exception = "none";
            try
            {
                if (padded)
                {
                    var owner = kind == "holder-null" ? null : wide;
                    result = operation == "find" ? (object)owner.Find(key) : owner.Contains(key);
                    repeated = operation == "find" ? (object)owner.Find(key) : owner.Contains(key);
                }
                else
                {
                    var owner = kind == "holder-null" ? null : simple;
                    result = operation == "find" ? (object)owner.Find(key) : owner.Contains(key);
                    repeated = operation == "find" ? (object)owner.Find(key) : owner.Contains(key);
                }
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "layout", padded ? "padded" : "simple" }, { "operation", operation },
                { "key", key }, { "result", result }, { "repeatedResult", repeated }, { "exception", exception },
                { "itemsBefore", before }, { "itemsAfter", State(items, padded) }, { "neighbor", wide.Neighbor }
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
