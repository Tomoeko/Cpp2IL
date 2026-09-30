using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SignedFieldComparisonFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly string[] Cases = { "distinct", "alias", "first-null", "second-null", "both-null", "comparer-null" };
        private static readonly int[,] Pairs =
        {
            { int.MinValue, int.MaxValue }, { int.MaxValue, int.MinValue },
            { int.MinValue, int.MinValue }, { int.MaxValue, int.MaxValue },
            { -1, 0 }, { 0, -1 }, { 0, 0 }, { 1, 0 }, { 0, 1 }
        };
        private static readonly Guid Tag = new Guid(17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59);

        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var methods = 0;
            var fields = 0;
            foreach (var type in new[] { typeof(ComparisonItem), typeof(PaddedComparisonItem), typeof(ComparisonOwner) })
            {
                methods += type.GetMethods(flags).Length + type.GetConstructors(flags).Length;
                fields += type.GetFields(flags).Length;
            }
            var signatures = new Dictionary<string, object>();
            foreach (var method in typeof(ComparisonOwner).GetMethods(flags))
                signatures.Add(method.Name, new[] { method.ReturnType.FullName,
                    method.GetParameters()[0].ParameterType.FullName, method.GetParameters()[1].ParameterType.FullName });
            var rows = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" }, { "methods", methods }, { "fields", fields },
                    { "keyType", typeof(ComparisonItem).GetField("Key").FieldType.FullName },
                    { "paddedKeyType", typeof(PaddedComparisonItem).GetField("Key").FieldType.FullName },
                    { "tagType", typeof(PaddedComparisonItem).GetField("Tag").FieldType.FullName }, { "signatures", signatures }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "key", new ComparisonItem().Key }, { "neighbor", new ComparisonItem().Neighbor },
                    { "paddedKey", new PaddedComparisonItem().Key }, { "tag", new PaddedComparisonItem().Tag.ToString() },
                    { "padding", Padding(new PaddedComparisonItem()) }
                }
            };
            foreach (var operation in new[] { "compare", "compareAgain", "comparePadded" })
                foreach (var kind in Cases)
                    for (var index = 0; index < Pairs.GetLength(0); index++)
                        Record(rows, operation, kind, Pairs[index, 0], Pairs[index, 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "signed-field-comparison" }, { "observations", rows }
            }));
        }

        private static long[] Padding(PaddedComparisonItem value)
        {
            return new[] { value.First, value.Second, value.Third, value.Fourth, value.Fifth, value.Sixth, value.Seventh };
        }

        private static object State(object value, object first, bool padded)
        {
            if (value == null) return null;
            var row = new Dictionary<string, object> { { "identity", ReferenceEquals(value, first) ? "first" : "second" } };
            if (padded)
            {
                var item = (PaddedComparisonItem)value;
                row.Add("key", item.Key); row.Add("tag", item.Tag.ToString()); row.Add("padding", Padding(item));
            }
            else
            {
                var item = (ComparisonItem)value;
                row.Add("key", item.Key); row.Add("neighbor", item.Neighbor);
            }
            return row;
        }

        private static object Item(int key, bool padded)
        {
            return padded ? (object)new PaddedComparisonItem { Key = key, Tag = Tag,
                First = 11, Second = 13, Third = 17, Fourth = 19, Fifth = 23, Sixth = 29, Seventh = 31 }
                : new ComparisonItem { Key = key, Neighbor = 43 };
        }

        private static int Invoke(ComparisonOwner owner, object first, object second, string operation)
        {
            switch (operation)
            {
                case "compare": return owner.Compare((ComparisonItem)first, (ComparisonItem)second);
                case "compareAgain": return owner.CompareAgain((ComparisonItem)first, (ComparisonItem)second);
                default: return owner.ComparePadded((PaddedComparisonItem)first, (PaddedComparisonItem)second);
            }
        }

        private static void Record(List<object> rows, string operation, string kind, int firstKey, int secondKey)
        {
            var padded = operation == "comparePadded";
            object first = Item(firstKey, padded), second = kind == "alias" ? first : Item(secondKey, padded);
            if (kind == "first-null" || kind == "both-null") first = null;
            if (kind == "second-null" || kind == "both-null") second = null;
            var owner = kind == "comparer-null" ? null : new ComparisonOwner();
            var firstBefore = State(first, first, padded); var secondBefore = State(second, first, padded);
            object result = null, repeatedResult = null;
            var exception = "none";
            try { result = Invoke(owner, first, second, operation); repeatedResult = Invoke(owner, first, second, operation); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", operation }, { "firstKey", firstKey }, { "secondKey", secondKey },
                { "result", result }, { "repeatedResult", repeatedResult }, { "exception", exception },
                { "firstBefore", firstBefore }, { "secondBefore", secondBefore },
                { "firstAfter", State(first, first, padded) }, { "secondAfter", State(second, first, padded) }
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
