using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using ClassReferenceSetterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.Instance;
            var cellType = typeof(Cell);
            var payloadType = typeof(Payload);
            var property = cellType.GetProperty("Current");
            var first = new Cell();
            var second = new Cell();
            var value = new Payload { Marker = 17 };
            var replacement = new Payload { Marker = -29 };
            var neighbor = new Payload { Marker = 41 };
            var alias = first;
            var rows = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" },
                    { "methods", cellType.GetMethods(flags).Length + cellType.GetConstructors(flags).Length +
                        payloadType.GetMethods(flags).Length + payloadType.GetConstructors(flags).Length },
                    { "fields", cellType.GetFields(flags).Length + payloadType.GetFields(flags).Length },
                    { "properties", cellType.GetProperties(flags).Length },
                    { "propertyType", property.PropertyType.FullName },
                    { "fieldType", cellType.GetField("_current", flags).FieldType.FullName },
                    { "fieldPrivate", cellType.GetField("_current", flags).IsPrivate },
                    { "getter", property.CanRead }, { "setter", property.CanWrite }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "firstNull", first.Current == null },
                    { "secondNull", second.Current == null }, { "marker", new Payload().Marker }
                },
                Row("distinct-cells", !ReferenceEquals(first, second)),
                Row("distinct-payloads", !ReferenceEquals(value, replacement))
            };

            first.Current = value;
            rows.Add(Row("first-identity", ReferenceEquals(first.Current, value)));
            rows.Add(Row("first-marker", first.Current.Marker == 17));
            rows.Add(Row("payload-neighbors-unchanged", replacement.Marker == -29 && neighbor.Marker == 41));
            rows.Add(Row("other-cell-null", second.Current == null));
            alias.Current = replacement;
            rows.Add(Row("alias-replacement", ReferenceEquals(first.Current, replacement)));
            rows.Add(Row("alias-readback", ReferenceEquals(alias.Current, replacement)));
            rows.Add(Row("replacement-marker", first.Current.Marker == -29));
            rows.Add(Row("old-payload-unchanged", value.Marker == 17));
            second.Current = value;
            rows.Add(Row("second-identity", ReferenceEquals(second.Current, value)));
            rows.Add(Row("first-cell-unchanged", ReferenceEquals(first.Current, replacement)));
            rows.Add(Row("second-marker", second.Current.Marker == 17));
            alias.Current = replacement;
            rows.Add(Row("repeat-identity", ReferenceEquals(first.Current, replacement)));
            rows.Add(Row("repeat-markers", value.Marker == 17 && replacement.Marker == -29 && neighbor.Marker == 41));
            first.Current = null;
            rows.Add(Row("clear-is-null", alias.Current == null));
            rows.Add(Row("clear-second-unchanged", ReferenceEquals(second.Current, value)));
            rows.Add(Row("clear-payloads-unchanged", value.Marker == 17 && replacement.Marker == -29 && neighbor.Marker == 41));
            first.Current = neighbor;
            second.Current = neighbor;
            rows.Add(Row("shared-first", ReferenceEquals(first.Current, neighbor)));
            rows.Add(Row("shared-second", ReferenceEquals(first.Current, second.Current)));
            rows.Add(Row("shared-marker-unchanged", neighbor.Marker == 41));
            first.Current = null;
            rows.Add(Row("cleared-first-does-not-clear-second", first.Current == null && ReferenceEquals(second.Current, neighbor)));

            Cell missing = null;
            rows.Add(ExceptionRow("null-owner-value", () => missing.Current = value));
            rows.Add(ExceptionRow("null-owner-null", () => missing.Current = null));
            rows.Add(ExceptionRow("null-owner-getter", () => GC.KeepAlive(missing.Current)));

            // Keep an older cell as the sole strong root of a newly assigned payload.
            // This exercises a reference store across collections without depending
            // on collection of any particular unreachable object.
            GC.Collect();
            var weak = InstallPayload(first);
            for (var index = 0; index < 64; index++)
                GC.KeepAlive(new byte[4096]);
            GC.Collect();
            rows.Add(Row("collection-retains-payload", weak.IsAlive));
            rows.Add(Row("collection-retains-identity", ReferenceEquals(first.Current, weak.Target)));
            rows.Add(Row("collection-retains-marker", first.Current != null && first.Current.Marker == 73));
            rows.Add(Row("collection-preserves-neighbors", ReferenceEquals(second.Current, neighbor) &&
                neighbor.Marker == 41 && value.Marker == 17 && replacement.Marker == -29));
            GC.KeepAlive(first);
            GC.KeepAlive(second);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "class-reference-setter" }, { "observations", rows }
            }));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference InstallPayload(Cell owner)
        {
            var payload = new Payload { Marker = 73 };
            owner.Current = payload;
            return new WeakReference(payload);
        }

        private static object Row(string check, bool result) => new Dictionary<string, object>
        {
            { "kind", "check" }, { "check", check }, { "result", result }
        };

        private static object ExceptionRow(string check, Action action)
        {
            var exception = "none";
            try { action(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "exception" }, { "check", check }, { "exception", exception }
            };
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
