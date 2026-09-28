using System;
using System.Collections.Generic;
using System.IO;
using FoldedReferenceArrayFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var length = 0; length <= 3; length++)
            {
                for (var index = 0; index <= 1; index++)
                {
                    observations.Add(ObserveA("length-" + length, index,
                        new CatalogA { Before = -53, Items = NewItems(length), After = 59 }));
                    observations.Add(ObserveB("length-" + length, index,
                        new CatalogB { Before = -53, Items = NewItems(length), After = 59 }));
                }
            }
            for (var index = 0; index <= 1; index++)
            {
                var first = NewItems(3);
                first[index] = null;
                observations.Add(ObserveA("element-null", index,
                    new CatalogA { Before = -53, Items = first, After = 59 }, index));
                var second = NewItems(3);
                second[index] = null;
                observations.Add(ObserveB("element-null", index,
                    new CatalogB { Before = -53, Items = second, After = 59 }, index));
                observations.Add(ObserveA("array-null", index,
                    new CatalogA { Before = -53, Items = null, After = 59 }));
                observations.Add(ObserveB("array-null", index,
                    new CatalogB { Before = -53, Items = null, After = 59 }));
                observations.Add(ObserveA("owner-null", index, null));
                observations.Add(ObserveB("owner-null", index, null));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "folded-reference-array" },
                { "observations", observations }
            }));
        }

        private static Cell[] NewItems(int length)
        {
            var result = new Cell[length];
            for (var index = 0; index < length; index++)
                result[index] = new Cell
                {
                    Id = 11 + index, Before = -37 - index, After = 41 + index
                };
            return result;
        }

        private static bool ItemsUnchanged(Cell[] items, int nullIndex)
        {
            for (var index = 0; index < items.Length; index++)
            {
                if (index == nullIndex)
                {
                    if (items[index] != null)
                        return false;
                }
                else if (items[index] == null || items[index].Id != 11 + index ||
                         items[index].Before != -37 - index ||
                         items[index].After != 41 + index)
                    return false;
            }
            return true;
        }

        private static object ObserveA(string kind, int index, CatalogA owner,
            int nullIndex = -1)
        {
            var items = owner == null ? null : owner.Items;
            return Observe(kind, "A", index, () => index == 0 ? owner.First : owner.Second,
                items, () => owner.Items, () => owner.Before, () => owner.After,
                owner != null, nullIndex);
        }

        private static object ObserveB(string kind, int index, CatalogB owner,
            int nullIndex = -1)
        {
            var items = owner == null ? null : owner.Items;
            return Observe(kind, "B", index, () => index == 0 ? owner.First : owner.Second,
                items, () => owner.Items, () => owner.Before, () => owner.After,
                owner != null, nullIndex);
        }

        private static object Observe(string kind, string ownerName, int index,
            Func<Cell> read, Cell[] items, Func<Cell[]> currentItems,
            Func<long> currentBefore, Func<long> currentAfter, bool hasOwner,
            int nullIndex)
        {
            Cell result = null;
            var exception = "none";
            try { result = read(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "owner", ownerName }, { "index", index },
                { "resultId", result == null ? null : (object)result.Id },
                { "exception", exception },
                { "sameElement", exception == "none" ?
                    (object)ReferenceEquals(result, items[index]) : null },
                { "sameArray", hasOwner ?
                    (object)ReferenceEquals(items, currentItems()) : null },
                { "ownerBefore", hasOwner ? (object)currentBefore() : null },
                { "ownerAfter", hasOwner ? (object)currentAfter() : null },
                { "itemsUnchanged", items == null ? null :
                    (object)ItemsUnchanged(items, nullIndex) }
            };
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report")
                    continue;
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
