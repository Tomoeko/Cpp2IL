using System;
using System.Collections.Generic;
using System.IO;
using FieldPlusOneReferenceArrayFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Positions =
        {
            -2, -1, 0, 1, 2, int.MinValue, int.MaxValue
        };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var length = 0; length <= 3; length++)
            {
                for (var index = 0; index < Positions.Length; index++)
                {
                    var position = Positions[index];
                    observations.Add(Observe("length-" + length, "A",
                        NewReaderA(NewItems(length), position)));
                    observations.Add(Observe("length-" + length, "B",
                        NewReaderB(NewItems(length), position)));
                }
            }

            foreach (var position in new[] { -1, 0, int.MaxValue })
            {
                observations.Add(Observe("array-null", "A",
                    NewReaderA(null, position)));
                observations.Add(Observe("array-null", "B",
                    NewReaderB(null, position)));
                observations.Add(Observe("owner-null", "A", null, position));
                observations.Add(Observe("owner-null", "B", null, position));
            }

            var aNull = NewItems(2);
            aNull[0] = null;
            var bNull = NewItems(2);
            bNull[0] = null;
            observations.Add(Observe("element-null", "A",
                NewReaderA(aNull, -1)));
            observations.Add(Observe("element-null", "B",
                NewReaderB(bNull, -1)));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "field-plus-one-reference-array" },
                { "observations", observations }
            }));
        }

        private static Cell[] NewItems(int length)
        {
            var items = new Cell[length];
            for (var index = 0; index < length; index++)
                items[index] = new Cell { Id = index + 11 };
            return items;
        }

        private static ReaderA NewReaderA(Cell[] items, int position)
        {
            return new ReaderA
            {
                Before = -53, Items = items, Position = position, After = 59
            };
        }

        private static ReaderB NewReaderB(Cell[] items, int position)
        {
            return new ReaderB
            {
                Before = -53, Spacer = -61, Items = items,
                Position = position, After = 59
            };
        }

        private static bool ItemsUnchanged(Cell[] items, bool nullElement)
        {
            for (var index = 0; index < items.Length; index++)
            {
                if (nullElement && index == 0)
                {
                    if (items[index] != null)
                        return false;
                }
                else if (items[index] == null || items[index].Id != index + 11)
                    return false;
            }
            return true;
        }

        private static object Observe(string kind, string ownerKind,
            object owner, int nullPosition = 0)
        {
            var a = owner as ReaderA;
            var b = owner as ReaderB;
            var items = a == null ? b == null ? null : b.Items : a.Items;
            var position = owner == null ? nullPosition : a == null ? b.Position : a.Position;
            Cell result = null;
            var exception = "none";
            try
            {
                result = ownerKind == "A" ? a.Next : b.Next;
            }
            catch (Exception error) { exception = error.GetType().FullName; }

            var selected = unchecked(position + 1);
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "owner", ownerKind },
                { "position", position },
                { "resultId", result == null ? null : (object)result.Id },
                { "exception", exception },
                { "sameElement", exception == "none" ?
                    (object)ReferenceEquals(result, items[selected]) : null },
                { "sameArray", owner == null ? null :
                    (object)ReferenceEquals(items, a == null ? b.Items : a.Items) },
                { "positionUnchanged", owner == null ? null :
                    (object)(position == (a == null ? b.Position : a.Position)) },
                { "before", owner == null ? null : (object)(a == null ? b.Before : a.Before) },
                { "spacer", b == null ? null : (object)b.Spacer },
                { "after", owner == null ? null : (object)(a == null ? b.After : a.After) },
                { "itemsUnchanged", items == null ? null :
                    (object)ItemsUnchanged(items, kind == "element-null") }
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
