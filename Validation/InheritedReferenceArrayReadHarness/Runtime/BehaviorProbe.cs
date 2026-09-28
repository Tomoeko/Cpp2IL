using System;
using System.Collections.Generic;
using System.IO;
using InheritedReferenceArrayReadFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly string[] ArrayKinds =
        {
            "owner-null", "array-null", "empty", "single", "two",
            "covariant-two", "alias-two"
        };

        public static void Write(string path, string stage)
        {
            var first = new Cell { Marker = 10 };
            var second = new Cell { Marker = 11 };
            var derivedFirst = new DerivedCell { Marker = 20 };
            var derivedSecond = new DerivedCell { Marker = 21 };
            var known = new Cell[] { first, second, derivedFirst, derivedSecond };
            var labels = new[] { "first", "second", "derived-first", "derived-second" };
            var observations = new List<object>();

            foreach (var getter in new[] { "first", "second" })
                foreach (var kind in ArrayKinds)
                {
                    Cell[] items = MakeArray(kind, first, second,
                        derivedFirst, derivedSecond);
                    DerivedCatalog owner = kind == "owner-null" ? null :
                        new DerivedCatalog { Before = -31, Items = items, After = 37 };
                    var before = Snapshot(items, known, labels);
                    Cell result = null;
                    var exception = "none";
                    try { result = getter == "first" ? owner.First : owner.Second; }
                    catch (Exception error) { exception = error.GetType().FullName; }
                    var index = getter == "first" ? 0 : 1;
                    observations.Add(new Dictionary<string, object>
                    {
                        { "getter", getter }, { "array", kind }, { "index", index },
                        { "exception", exception },
                        { "result", exception == "none" ? Identify(result, known, labels) : null },
                        { "sameReference", exception == "none" && items != null &&
                            index < items.Length ? (object)ReferenceEquals(result, items[index]) : null },
                        { "before", before }, { "after", Snapshot(items, known, labels) },
                        { "sameArray", owner == null ? null :
                            (object)ReferenceEquals(owner.Items, items) },
                        { "ownerBefore", owner == null ? null : (object)owner.Before },
                        { "ownerAfter", owner == null ? null : (object)owner.After }
                    });
                }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "inherited-reference-array-read" },
                { "observations", observations }
            }));
        }

        private static Cell[] MakeArray(string kind, Cell first, Cell second,
            DerivedCell derivedFirst, DerivedCell derivedSecond)
        {
            switch (kind)
            {
                case "empty": return new Cell[0];
                case "single": return new[] { first };
                case "two": return new[] { first, second };
                case "covariant-two": return new[] { derivedFirst, derivedSecond };
                case "alias-two": return new[] { first, first };
                default: return null;
            }
        }

        private static string[] Snapshot(Cell[] items, Cell[] known,
            string[] labels)
        {
            if (items == null) return null;
            var result = new string[items.Length];
            for (var index = 0; index < items.Length; index++)
                result[index] = Identify(items[index], known, labels);
            return result;
        }

        private static string Identify(Cell value, Cell[] known,
            string[] labels)
        {
            if (value == null) return "null";
            for (var index = 0; index < known.Length; index++)
                if (ReferenceEquals(value, known[index])) return labels[index];
            return "unknown";
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
