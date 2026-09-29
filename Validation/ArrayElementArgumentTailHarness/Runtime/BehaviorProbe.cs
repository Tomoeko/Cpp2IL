using System;
using System.Collections.Generic;
using System.IO;
using ArrayElementArgumentTailFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object> { Constructor() };
            foreach (var index in new[] { -1, 0, 2 })
            {
                observations.Add(NullOwner(index));
                observations.Add(Invoke("array-null", index));
            }
            foreach (var index in new[] { -1, 0, int.MaxValue })
                observations.Add(Invoke("length-zero", index));
            foreach (var index in new[] { int.MinValue, -1, 0, 1, 2, int.MaxValue })
                observations.Add(Invoke("length-two", index));
            foreach (var scenario in new[]
                { "element-null", "value-null", "element-alias", "value-alias" })
                foreach (var index in new[] { 0, 1 })
                    observations.Add(Invoke(scenario, index));
            observations.AddRange(Sequence());

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "array-element-argument-tail" },
                { "observations", observations }
            }));
        }

        private static object Constructor()
        {
            var host = new Host();
            var item = new Item();
            var value = new ValueBox();
            return new Dictionary<string, object>
            {
                { "kind", "constructor" },
                { "itemsNull", host.Items == null },
                { "callCount", host.CallCount },
                { "lastNull", host.LastValue == null },
                { "neighbor", host.Neighbor },
                { "valueNull", item.Value == null },
                { "itemSentinel", item.Sentinel },
                { "valueId", value.Id }
            };
        }

        private static object NullOwner(int index)
        {
            var failure = "none";
            try { ((Host)null).Forward(index); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "null-owner" }, { "index", index }, { "failure", failure }
            };
        }

        private static object Invoke(string scenario, int index)
        {
            var seed = new ValueBox { Id = 101 };
            var first = new ValueBox { Id = 11 };
            var second = new ValueBox { Id = 23 };
            var items = CreateItems(scenario, first, second);
            var elementsBefore = items == null ? null : (Item[])items.Clone();
            var valuesBefore = items == null ? null : new ValueBox[items.Length];
            if (items != null)
                for (var position = 0; position < items.Length; position++)
                    valuesBefore[position] = items[position] == null ? null : items[position].Value;
            var host = new Host
                { Items = items, CallCount = 7, LastValue = seed, Neighbor = 83 };
            var failure = "none";
            try { host.Forward(index); }
            catch (Exception error) { failure = error.GetType().FullName; }

            var sentinels = new List<object>();
            var valueIds = new List<object>();
            var sameElements = true;
            var sameValues = true;
            if (items != null)
                for (var position = 0; position < items.Length; position++)
                {
                    var item = items[position];
                    sameElements &= ReferenceEquals(item, elementsBefore[position]);
                    sameValues &= ReferenceEquals(item == null ? null : item.Value,
                        valuesBefore[position]);
                    sentinels.Add(item == null ? null : (object)item.Sentinel);
                    valueIds.Add(item == null || item.Value == null
                        ? null : (object)item.Value.Id);
                }
            return new Dictionary<string, object>
            {
                { "kind", "invoke" }, { "scenario", scenario }, { "index", index },
                { "failure", failure }, { "callCount", host.CallCount },
                { "last", Identity(host.LastValue, seed, first, second) },
                { "sameArray", ReferenceEquals(host.Items, items) },
                { "sameElements", items == null ? null : (object)sameElements },
                { "sameValues", items == null ? null : (object)sameValues },
                { "elementsAliased", items == null || items.Length < 2
                    ? null : (object)ReferenceEquals(items[0], items[1]) },
                { "valuesAliased", items == null || items.Length < 2
                    ? null : (object)ReferenceEquals(items[0] == null ? null : items[0].Value,
                        items[1] == null ? null : items[1].Value) },
                { "itemSentinels", items == null ? null : (object)sentinels },
                { "valueIds", items == null ? null : (object)valueIds },
                { "neighbor", host.Neighbor }
            };
        }

        private static Item[] CreateItems(string scenario, ValueBox first, ValueBox second)
        {
            if (scenario == "array-null") return null;
            if (scenario == "length-zero") return new Item[0];
            var firstItem = new Item { Value = first, Sentinel = 17 };
            var secondItem = new Item { Value = second, Sentinel = 53 };
            if (scenario == "element-null") firstItem = null;
            if (scenario == "value-null") firstItem.Value = null;
            if (scenario == "element-alias") secondItem = firstItem;
            if (scenario == "value-alias") secondItem.Value = first;
            return new[] { firstItem, secondItem };
        }

        private static List<object> Sequence()
        {
            var seed = new ValueBox { Id = 101 };
            var first = new ValueBox { Id = 11 };
            var second = new ValueBox { Id = 23 };
            var items = new[]
            {
                new Item { Value = first, Sentinel = 17 },
                new Item { Value = second, Sentinel = 53 },
                new Item { Value = null, Sentinel = 71 }
            };
            var original = (Item[])items.Clone();
            var host = new Host
            {
                Items = items, CallCount = int.MaxValue - 1,
                LastValue = seed, Neighbor = 83
            };
            var rows = new List<object>
            {
                Step(host, items, original, seed, first, second, "first", 0),
                Step(host, items, original, seed, first, second, "second", 1),
                Step(host, items, original, seed, first, second, "invalid", -1),
                Step(host, items, original, seed, first, second, "null-value", 2)
            };
            items[0].Value = second;
            rows.Add(Step(host, items, original, seed, first, second, "changed-value", 0));
            return rows;
        }

        private static object Step(Host host, Item[] items, Item[] original,
            ValueBox seed, ValueBox first, ValueBox second, string step, int index)
        {
            var failure = "none";
            try { host.Forward(index); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "sequence" }, { "step", step }, { "index", index },
                { "failure", failure }, { "callCount", host.CallCount },
                { "last", Identity(host.LastValue, seed, first, second) },
                { "firstValueId", items[0].Value.Id },
                { "secondValueId", items[1].Value.Id },
                { "thirdValueNull", items[2].Value == null },
                { "sameArray", ReferenceEquals(host.Items, items) },
                { "sameElements", ReferenceEquals(items[0], original[0]) &&
                    ReferenceEquals(items[1], original[1]) &&
                    ReferenceEquals(items[2], original[2]) },
                { "neighbor", host.Neighbor }
            };
        }

        private static string Identity(ValueBox value, ValueBox seed,
            ValueBox first, ValueBox second)
        {
            if (value == null) return "null";
            if (ReferenceEquals(value, seed)) return "seed";
            if (ReferenceEquals(value, first)) return "first";
            if (ReferenceEquals(value, second)) return "second";
            return "unexpected";
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report") continue;
                try
                {
                    Write(arguments[index + 1], "player");
                    Application.Quit(0);
                }
                catch (Exception error)
                {
                    Debug.LogException(error);
                    Application.Quit(1);
                }
                return;
            }
        }
    }
}
