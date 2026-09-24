using System;
using System.Collections.Generic;
using System.IO;
using InstanceReferencePropertyFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var neighbor = new object();
            var first = new ReferenceCell { Neighbor = neighbor, Marker = 23 };
            var second = new ReferenceCell { Neighbor = new object(), Marker = -23 };
            var alias = first;
            var firstValue = new object();
            var replacement = new object();
            var observations = new List<object>
            {
                Row("reference", "starts-null-field", first.Stored == null),
                Row("reference", "getter-starts-null", first.Value == null),
                Row("reference", "distinct-cells", !ReferenceEquals(first, second))
            };

            first.Value = firstValue;
            observations.Add(Row("reference", "first-field-identity", ReferenceEquals(first.Stored, firstValue)));
            observations.Add(Row("reference", "first-getter-identity", ReferenceEquals(first.Value, firstValue)));
            observations.Add(Row("reference", "neighbor-untouched", ReferenceEquals(first.Neighbor, neighbor)));
            observations.Add(Row("reference", "marker-untouched", first.Marker == 23));
            observations.Add(Row("reference", "other-cell-untouched", second.Stored == null && second.Marker == -23));

            alias.Value = replacement;
            observations.Add(Row("reference", "alias-field-replacement", ReferenceEquals(first.Stored, replacement)));
            observations.Add(Row("reference", "alias-getter-replacement", ReferenceEquals(first.Value, replacement)));
            observations.Add(Row("reference", "old-value-replaced", !ReferenceEquals(first.Value, firstValue)));
            second.Value = firstValue;
            observations.Add(Row("reference", "separate-cell-getter", ReferenceEquals(second.Value, firstValue)));
            observations.Add(Row("reference", "first-cell-unchanged", ReferenceEquals(first.Value, replacement)));
            observations.Add(Row("reference", "separate-neighbor-marker", second.Neighbor != null && second.Marker == -23));

            first.Value = first;
            observations.Add(Row("reference", "self-reference-getter", ReferenceEquals(first.Value, alias)));
            first.Value = null;
            observations.Add(Row("reference", "null-clears-field", first.Stored == null));
            observations.Add(Row("reference", "null-clears-getter", first.Value == null));
            observations.Add(Row("reference", "neighbor-after-clear", ReferenceEquals(first.Neighbor, neighbor)));
            observations.Add(Row("reference", "marker-after-clear", first.Marker == 23));

            ReferenceCell missing = null;
            observations.Add(ExceptionRow("reference", "null-setter-value", () => missing.Value = firstValue));
            observations.Add(ExceptionRow("reference", "null-setter-null", () => missing.Value = null));
            observations.Add(ExceptionRow("reference", "null-getter", () => GC.KeepAlive(missing.Value)));

            var textNeighbor = new object();
            var text = new TextCell { Neighbor = textNeighbor, Marker = 31 };
            var originalText = new string(new[] { 'a', 'b' });
            var replacementText = new string(new[] { 'c', 'd' });
            observations.Add(Row("text", "starts-null", text.Stored == null));
            text.Value = originalText;
            observations.Add(Row("text", "first-field-identity", ReferenceEquals(text.Stored, originalText)));
            observations.Add(Row("text", "neighbor-untouched", ReferenceEquals(text.Neighbor, textNeighbor)));
            text.Value = replacementText;
            observations.Add(Row("text", "replacement-field-identity", ReferenceEquals(text.Stored, replacementText)));
            observations.Add(Row("text", "marker-untouched", text.Marker == 31));
            text.Value = null;
            observations.Add(Row("text", "null-clears-field", text.Stored == null));
            observations.Add(Row("text", "neighbor-after-clear", ReferenceEquals(text.Neighbor, textNeighbor)));

            TextCell missingText = null;
            observations.Add(ExceptionRow("text", "null-setter", () => missingText.Value = originalText));

            var referenceProperty = typeof(ReferenceCell).GetProperty("Value");
            var textProperty = typeof(TextCell).GetProperty("Value");
            observations.Add(Row("declaration", "reference-property-type",
                referenceProperty != null && referenceProperty.PropertyType == typeof(object)));
            observations.Add(Row("declaration", "reference-read-write",
                referenceProperty != null && referenceProperty.CanRead && referenceProperty.CanWrite));
            observations.Add(Row("declaration", "text-property-type",
                textProperty != null && textProperty.PropertyType == typeof(string)));
            observations.Add(Row("declaration", "text-setter-only",
                textProperty != null && !textProperty.CanRead && textProperty.CanWrite));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "instance-reference-property" },
                { "observations", observations }
            }));
        }

        private static object Row(string subject, string check, bool result) =>
            new Dictionary<string, object>
            {
                { "subject", subject }, { "check", check }, { "result", result }
            };

        private static object ExceptionRow(string subject, string check, Action action)
        {
            var exception = "none";
            try { action(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "subject", subject }, { "check", check }, { "exception", exception }
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
