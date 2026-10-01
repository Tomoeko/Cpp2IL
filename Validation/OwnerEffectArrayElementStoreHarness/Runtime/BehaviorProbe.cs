using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using OwnerEffectArrayElementStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Indices = { int.MinValue, -1, 0, 1, 2, 3, int.MaxValue };
        private static readonly string[] Cases =
        {
            "aliased", "element-null", "element-null-negative", "array-null", "array-null-minimum", "owner-null", "array-empty"
        };

        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var methods = 0;
            var fields = 0;
            var properties = 0;
            var signatures = new Dictionary<string, object>();
            var fieldTypes = new Dictionary<string, object>();
            var fieldAccess = new Dictionary<string, object>();
            var types = new[] { typeof(Cell), typeof(Catalog) };
            foreach (var type in types)
            {
                methods += type.GetMethods(flags).Length + type.GetConstructors(flags).Length;
                fields += type.GetFields(flags).Length;
                properties += type.GetProperties(flags).Length;
                foreach (var method in type.GetMethods(flags))
                {
                    var signature = new List<string> { method.ReturnType.FullName };
                    foreach (var parameter in method.GetParameters()) signature.Add(parameter.ParameterType.FullName);
                    signatures.Add(type.Name + "." + method.Name, signature);
                }
                foreach (var field in type.GetFields(flags))
                {
                    fieldTypes.Add(type.Name + "." + field.Name, field.FieldType.FullName);
                    fieldAccess.Add(type.Name + "." + field.Name, (field.Attributes & FieldAttributes.FieldAccessMask).ToString());
                }
            }
            var defaultCell = new Cell();
            var defaultCatalog = new Catalog();
            var rows = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" }, { "methods", methods }, { "fields", fields },
                    { "properties", properties }, { "types", types.Length }, { "signatures", signatures },
                    { "fieldTypes", fieldTypes }, { "fieldAccess", fieldAccess },
                    { "sealedTypes", new[] { typeof(Cell).IsSealed, typeof(Catalog).IsSealed } }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "cell", CellState(defaultCell) },
                    { "catalog", CatalogState(defaultCatalog, null, null, null, null, null) }
                }
            };
            foreach (var active in new[] { false, true })
            {
                foreach (var index in Indices) Fresh(rows, "length-three", index, active);
                foreach (var kind in Cases)
                {
                    var index = kind == "element-null" ? 1 : kind == "element-null-negative" ? -1 :
                        kind == "array-null-minimum" ? int.MinValue : 0;
                    Fresh(rows, kind, index, active);
                }
            }
            var firstCell = new Cell { Before = 17, Enabled = true, After = 83 };
            var secondCell = new Cell { Before = 29, Enabled = false, After = 101 };
            var thirdCell = new Cell { Before = 41, Enabled = true, After = 113 };
            var oldArray = new[] { firstCell, secondCell, thirdCell };
            var replacementArray = new[] { secondCell, firstCell, thirdCell };
            var first = new Catalog { Before = 43, Active = true, After = 127, Items = oldArray };
            var second = new Catalog { Before = 59, Active = false, After = 139, Items = replacementArray };
            Record(rows, "reuse-first", first, 0, first, second, oldArray, replacementArray, firstCell, secondCell, thirdCell);
            oldArray[0] = null;
            first.Active = true;
            Record(rows, "reuse-null-element", first, 0, first, second, oldArray, replacementArray, firstCell, secondCell, thirdCell);
            oldArray[0] = firstCell;
            firstCell.Enabled = true;
            secondCell.Enabled = true;
            first.Items = replacementArray;
            first.Active = true;
            Record(rows, "reuse-replaced-array", first, 0, first, second, oldArray, replacementArray, firstCell, secondCell, thirdCell);
            first.Active = true;
            second.Active = true;
            Record(rows, "shared-other-owner", second, 1, first, second, oldArray, replacementArray, firstCell, secondCell, thirdCell);
            first.Items = null;
            first.Active = true;
            Record(rows, "reuse-null-array", first, 0, first, second, oldArray, replacementArray, firstCell, secondCell, thirdCell);
            first.Items = replacementArray;
            first.Active = true;
            Record(rows, "reuse-negative-bound", first, -1, first, second, oldArray, replacementArray, firstCell, secondCell, thirdCell);
            replacementArray[1] = thirdCell;
            second.Active = true;
            Record(rows, "shared-replaced-element", second, 1, first, second, oldArray, replacementArray, firstCell, secondCell, thirdCell);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "owner-effect-array-element-store" }, { "observations", rows }
            }));
        }

        private static void Fresh(List<object> rows, string kind, int index, bool active)
        {
            var firstCell = new Cell { Before = 17, Enabled = true, After = 83 };
            var secondCell = new Cell { Before = 29, Enabled = false, After = 101 };
            var thirdCell = new Cell { Before = 41, Enabled = true, After = 113 };
            var firstArray = kind == "array-null" || kind == "array-null-minimum" ? null :
                kind == "array-empty" ? new Cell[0] : new[] { firstCell, secondCell, thirdCell };
            var secondArray = new[] { thirdCell, firstCell, secondCell };
            if (kind == "aliased") firstArray[1] = firstCell;
            if (kind == "element-null" || kind == "element-null-negative") firstArray[1] = null;
            var first = new Catalog { Before = 43, Active = active, After = 127, Items = firstArray };
            var second = new Catalog { Before = 59, Active = !active, After = 139, Items = secondArray };
            Record(rows, kind, kind == "owner-null" ? null : first, index,
                first, second, firstArray, secondArray, firstCell, secondCell, thirdCell);
        }

        private static string Identity(object value, object first, object second, object third = null)
        {
            if (value == null) return "null";
            if (ReferenceEquals(value, first)) return "first";
            if (ReferenceEquals(value, second)) return "second";
            return ReferenceEquals(value, third) ? "third" : "other";
        }

        private static object ArrayValues(Cell[] items, Cell first, Cell second, Cell third)
        {
            if (items == null) return null;
            var values = new List<string>();
            foreach (var item in items) values.Add(Identity(item, first, second, third));
            return values;
        }

        private static object CellState(Cell cell)
        {
            return new Dictionary<string, object>
            {
                { "before", (int)cell.Before }, { "enabled", cell.Enabled }, { "after", (int)cell.After }
            };
        }

        private static object CatalogState(Catalog catalog, Cell[] firstArray, Cell[] secondArray, Cell first, Cell second, Cell third)
        {
            return new Dictionary<string, object>
            {
                { "before", catalog.Before }, { "active", catalog.Active }, { "after", (int)catalog.After },
                { "items", Identity(catalog.Items, firstArray, secondArray) }, { "itemValues", ArrayValues(catalog.Items, first, second, third) }
            };
        }

        private static object State(Catalog first, Catalog second, Cell[] firstArray, Cell[] secondArray,
            Cell firstCell, Cell secondCell, Cell thirdCell)
        {
            return new Dictionary<string, object>
            {
                { "first", CatalogState(first, firstArray, secondArray, firstCell, secondCell, thirdCell) },
                { "second", CatalogState(second, firstArray, secondArray, firstCell, secondCell, thirdCell) },
                { "firstCell", CellState(firstCell) }, { "secondCell", CellState(secondCell) }, { "thirdCell", CellState(thirdCell) },
                { "firstArray", ArrayValues(firstArray, firstCell, secondCell, thirdCell) },
                { "secondArray", ArrayValues(secondArray, firstCell, secondCell, thirdCell) }
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(Catalog owner, int index) => owner.Clear(index);

        private static void Record(List<object> rows, string kind, Catalog owner, int index, Catalog first, Catalog second,
            Cell[] firstArray, Cell[] secondArray, Cell firstCell, Cell secondCell, Cell thirdCell)
        {
            var before = State(first, second, firstArray, secondArray, firstCell, secondCell, thirdCell);
            var exception = "none";
            try { Invoke(owner, index); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "receiver", Identity(owner, first, second) }, { "index", index },
                { "exception", exception }, { "before", before },
                { "after", State(first, second, firstArray, secondArray, firstCell, secondCell, thirdCell) }
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
