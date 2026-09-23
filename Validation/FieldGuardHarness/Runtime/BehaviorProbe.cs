using System;
using System.Collections.Generic;
using System.IO;
using FieldGuardFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var value in new[] { int.MinValue, -17, 0, 17, int.MaxValue })
                Record(observations, value.ToString(), new FieldBox(value));
            Record(observations, "null", null);
            foreach (var value in new[] { int.MinValue, -17, 0, 17, int.MaxValue })
                RecordWrite(observations, value.ToString(), new FieldBox(42), value);
            RecordWrite(observations, "null", null, 17);
            foreach (var initial in new[] { int.MinValue, 0, int.MaxValue })
                RecordClear(observations, initial.ToString(), new FieldBox(initial));
            RecordClear(observations, "null", null);
            foreach (var value in new[] { long.MinValue, -17L, 0L, 17L, long.MaxValue })
                RecordLong(observations, value.ToString(), new LongBox(value));
            RecordLong(observations, "null", null);
            foreach (var value in new[] { long.MinValue, -17L, 0L, 17L, long.MaxValue })
                RecordLongWrite(observations, value.ToString(), new LongBox(42), value);
            RecordLongWrite(observations, "null", null, 17);
            var booleanReader = new BooleanReader();
            foreach (var initial in new[] { false, true })
            {
                var box = new BooleanBox { Value = initial };
                RecordBooleanRead(observations, initial.ToString(), box);
                RecordBooleanInstanceRead(observations, initial.ToString(), booleanReader, box);
                RecordBooleanClear(observations, initial.ToString(), box);
            }
            RecordBooleanRead(observations, "null", null);
            RecordBooleanInstanceRead(observations, "box-null", booleanReader, null);
            RecordBooleanInstanceRead(observations, "reader-null", null, new BooleanBox());
            RecordBooleanClear(observations, "null", null);
            foreach (var initial in new[] { int.MinValue, 0, int.MaxValue })
                RecordNestedClear(observations, initial.ToString(),
                    new NestedFieldBox { Inner = new FieldBox(initial) });
            RecordNestedClear(observations, "inner-null", new NestedFieldBox());
            RecordNestedClear(observations, "outer-null", null);
            foreach (var initial in new[] { false, true })
            {
                RecordNestedBooleanLiteral(observations, "true:" + initial,
                    new NestedBooleanOwner
                    {
                        Inner = new NestedBooleanBox { Value = initial, Neighbor = -82 },
                        Neighbor = 81
                    }, true);
                RecordNestedBooleanLiteral(observations, "false:" + initial,
                    new NestedBooleanOwner
                    {
                        Inner = new NestedBooleanBox { Value = initial, Neighbor = -82 },
                        Neighbor = 81
                    }, false);
            }
            foreach (var setValue in new[] { true, false })
            {
                RecordNestedBooleanLiteral(observations, (setValue ? "true" : "false") + ":inner-null",
                    new NestedBooleanOwner { Neighbor = 81 }, setValue);
                RecordNestedBooleanLiteral(observations, (setValue ? "true" : "false") + ":outer-null",
                    null, setValue);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "field-guard" }, { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, FieldBox box)
        {
            object result = null;
            var exception = "none";
            try { result = FieldReads.Read(box); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "result", result }, { "exception", exception }
            });
        }

        private static void RecordWrite(List<object> observations, string kind, FieldBox box, int value)
        {
            object result = null;
            var exception = "none";
            try
            {
                FieldWrites.Write(box, value);
                result = box == null ? null : (object)box.Value;
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "write:" + kind }, { "result", result }, { "exception", exception }
            });
        }

        private static void RecordClear(List<object> observations, string kind, FieldBox box)
        {
            object result = null;
            var exception = "none";
            try
            {
                FieldClears.Clear(box);
                result = box == null ? null : (object)box.Value;
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "clear:" + kind }, { "result", result }, { "exception", exception }
            });
        }

        private static void RecordLong(List<object> observations, string kind, LongBox box)
        {
            object result = null;
            var exception = "none";
            try { result = LongFieldReads.Read(box); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "long:" + kind }, { "result", result }, { "exception", exception }
            });
        }

        private static void RecordLongWrite(List<object> observations, string kind, LongBox box, long value)
        {
            object result = null;
            var exception = "none";
            try
            {
                LongFieldWrites.Write(box, value);
                result = box == null ? null : (object)box.Value;
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "long-write:" + kind }, { "result", result }, { "exception", exception }
            });
        }

        private static void RecordBooleanRead(List<object> observations, string kind, BooleanBox box)
        {
            object result = null;
            var exception = "none";
            try { result = BooleanFieldReads.Read(box); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "bool-read:" + kind }, { "result", result }, { "exception", exception }
            });
        }

        private static void RecordBooleanInstanceRead(List<object> observations, string kind,
            BooleanReader reader, BooleanBox box)
        {
            object result = null;
            var exception = "none";
            try { result = reader.Read(box); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "bool-instance-read:" + kind }, { "result", result },
                { "exception", exception }
            });
        }

        private static void RecordBooleanClear(List<object> observations, string kind, BooleanBox box)
        {
            object result = null;
            var exception = "none";
            try
            {
                BooleanFieldClears.Clear(box);
                result = box == null ? null : (object)box.Value;
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "bool-clear:" + kind }, { "result", result }, { "exception", exception }
            });
        }

        private static void RecordNestedClear(List<object> observations, string kind, NestedFieldBox outer)
        {
            object result = null;
            var exception = "none";
            try
            {
                outer.ClearInner();
                result = outer.Inner == null ? null : (object)outer.Inner.Value;
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "nested-clear:" + kind }, { "result", result }, { "exception", exception }
            });
        }

        private static void RecordNestedBooleanLiteral(List<object> observations, string kind,
            NestedBooleanOwner outer, bool setValue)
        {
            var exception = "none";
            try
            {
                if (setValue) outer.SetTrue();
                else outer.SetFalse();
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "nested-bool:" + kind },
                { "result", outer != null && outer.Inner != null ? (object)outer.Inner.Value : null },
                { "ownerNeighbor", outer != null ? (object)outer.Neighbor : null },
                { "innerNeighbor", outer != null && outer.Inner != null ? (object)outer.Inner.Neighbor : null },
                { "exception", exception }
            });
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
