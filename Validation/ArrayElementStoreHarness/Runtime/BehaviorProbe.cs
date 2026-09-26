using System;
using System.Collections.Generic;
using System.IO;
using ArrayElementStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var enable in new[] { false, true })
            {
                foreach (var index in new[] { int.MinValue, -1, 0, 1, 2, 3, int.MaxValue })
                    observations.Add(Observe("distinct", index, enable));
                observations.Add(Observe("aliased", 1, enable));
                observations.Add(Observe("element-null", 1, enable));
                observations.Add(Observe("element-null", -1, enable));
                observations.Add(Observe("array-null", 1, enable));
                observations.Add(Observe("array-null", int.MinValue, enable));
                observations.Add(Observe("owner-null", 1, enable));
                observations.Add(Observe("empty", 0, enable));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "array-element-store" },
                { "observations", observations }
            }));
        }

        private static object Observe(string kind, int index, bool enable)
        {
            var items = new[]
            {
                new Cell { Before = 17, Enabled = !enable, After = 23 },
                new Cell { Before = 17, Enabled = !enable, After = 23 },
                new Cell { Before = 17, Enabled = !enable, After = 23 }
            };
            if (kind == "aliased")
                items[2] = items[1];
            if (kind == "element-null")
                items[1] = null;
            if (kind == "empty")
                items = new Cell[0];
            var owner = kind == "owner-null" ? null : new CellCatalog
            {
                Before = -29, Items = kind == "array-null" ? null : items, After = 31
            };
            var originalItems = owner == null ? null : owner.Items;
            var exception = "none";
            try
            {
                if (enable)
                    owner.Enable(index);
                else
                    owner.Disable(index);
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            var states = new List<object>();
            var neighborsUnchanged = true;
            foreach (var cell in items)
            {
                states.Add(cell == null ? null : (object)cell.Enabled);
                if (cell != null && (cell.Before != 17 || cell.After != 23))
                    neighborsUnchanged = false;
            }
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "index", index }, { "enable", enable },
                { "exception", exception }, { "states", states },
                { "neighborsUnchanged", neighborsUnchanged },
                { "sameArray", owner == null ? null : (object)ReferenceEquals(originalItems, owner.Items) },
                { "sameAlias", kind == "aliased" ? (object)ReferenceEquals(items[1], items[2]) : null },
                { "ownerBefore", owner == null ? null : (object)owner.Before },
                { "ownerAfter", owner == null ? null : (object)owner.After }
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
