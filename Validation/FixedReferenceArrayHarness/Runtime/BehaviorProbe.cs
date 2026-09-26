using System;
using System.Collections.Generic;
using System.IO;
using FixedReferenceArrayFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var length = 0; length <= 5; length++)
            {
                var items = NewItems(length);
                var catalog = new CellCatalog
                {
                    Before = -53, Items = items, After = 59
                };
                for (var index = 2; index <= 4; index++)
                    observations.Add(Observe("length-" + length, index, catalog));
            }
            for (var index = 2; index <= 4; index++)
            {
                var items = NewItems(5);
                items[index] = null;
                var catalog = new CellCatalog
                {
                    Before = -53, Items = items, After = 59
                };
                observations.Add(Observe("element-null", index, catalog, index));
            }
            var missingItems = new CellCatalog
            {
                Before = -53, Items = null, After = 59
            };
            for (var index = 2; index <= 4; index++)
            {
                observations.Add(Observe("array-null", index, missingItems));
                observations.Add(Observe("owner-null", index, null));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "fixed-reference-array" },
                { "observations", observations }
            }));
        }

        private static Cell[] NewItems(int length)
        {
            var items = new Cell[length];
            for (var index = 0; index < items.Length; index++)
                items[index] = new Cell
                {
                    Id = index + 11, Before = -37 - index, After = 41 + index
                };
            return items;
        }

        private static bool ItemsUnchanged(Cell[] items, int nullIndex)
        {
            for (var index = 0; index < items.Length; index++)
            {
                if (index == nullIndex)
                {
                    if (items[index] != null)
                        return false;
                    continue;
                }
                if (items[index] == null || items[index].Id != index + 11 ||
                    items[index].Before != -37 - index || items[index].After != 41 + index)
                    return false;
            }
            return true;
        }

        private static object Observe(string kind, int index, CellCatalog owner,
            int nullIndex = -1)
        {
            var items = owner == null ? null : owner.Items;
            Cell result = null;
            var exception = "none";
            try
            {
                switch (index)
                {
                    case 2: result = owner.Third; break;
                    case 3: result = owner.Fourth; break;
                    case 4: result = owner.Fifth; break;
                }
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "index", index },
                { "resultId", result == null ? null : (object)result.Id },
                { "exception", exception },
                { "sameElement", exception != "none" ? null :
                    (object)ReferenceEquals(result, items[index]) },
                { "sameArray", owner == null ? null : (object)ReferenceEquals(items, owner.Items) },
                { "ownerBefore", owner == null ? null : (object)owner.Before },
                { "ownerAfter", owner == null ? null : (object)owner.After },
                { "itemsUnchanged", items == null ? null : (object)ItemsUnchanged(items, nullIndex) }
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
