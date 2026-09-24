using System;
using System.Collections.Generic;
using System.IO;
using InstanceReferenceSetterFixture;
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
            var secondValue = new object();
            var observations = new List<object>
            {
                Row("reference", "starts-null", first.Stored == null && second.Stored == null),
                Row("reference", "distinct-cells", !ReferenceEquals(first, second)),
            };

            first.Value = firstValue;
            observations.Add(Row("reference", "first-value-identity", ReferenceEquals(first.Stored, firstValue)));
            observations.Add(Row("reference", "neighbor-untouched", ReferenceEquals(first.Neighbor, neighbor)));
            observations.Add(Row("reference", "marker-untouched", first.Marker == 23));
            observations.Add(Row("reference", "other-cell-untouched", second.Stored == null && second.Marker == -23));

            alias.Value = secondValue;
            observations.Add(Row("reference", "alias-replacement", ReferenceEquals(first.Stored, secondValue)));
            observations.Add(Row("reference", "old-value-replaced", !ReferenceEquals(first.Stored, firstValue)));
            second.Value = firstValue;
            observations.Add(Row("reference", "separate-cell-value", ReferenceEquals(second.Stored, firstValue)));
            observations.Add(Row("reference", "separate-cell-neighbor", second.Neighbor != null));

            first.Value = first;
            observations.Add(Row("reference", "self-reference", ReferenceEquals(first.Stored, alias)));
            first.Value = null;
            observations.Add(Row("reference", "null-clears-value", first.Stored == null));
            observations.Add(Row("reference", "neighbor-after-clear", ReferenceEquals(first.Neighbor, neighbor)));

            ReferenceCell missing = null;
            observations.Add(ExceptionRow("reference", "null-receiver-value", () => missing.Value = firstValue));
            observations.Add(ExceptionRow("reference", "null-receiver-null", () => missing.Value = null));

            var textNeighbor = new object();
            var text = new TextCell { Neighbor = textNeighbor, Marker = 31 };
            var originalText = new string(new[] { 'a', 'b' });
            var replacementText = new string(new[] { 'c', 'd' });
            observations.Add(Row("text", "starts-null", text.Stored == null));
            text.Value = originalText;
            observations.Add(Row("text", "value-identity", ReferenceEquals(text.Stored, originalText)));
            observations.Add(Row("text", "neighbor-untouched", ReferenceEquals(text.Neighbor, textNeighbor)));
            text.Value = replacementText;
            observations.Add(Row("text", "replacement-identity", ReferenceEquals(text.Stored, replacementText)));
            observations.Add(Row("text", "marker-untouched", text.Marker == 31));
            text.Value = null;
            observations.Add(Row("text", "null-clears-value", text.Stored == null));
            observations.Add(Row("text", "neighbor-after-clear", ReferenceEquals(text.Neighbor, textNeighbor)));

            TextCell missingText = null;
            observations.Add(ExceptionRow("text", "null-receiver", () => missingText.Value = originalText));
            observations.Add(Row("declaration", "reference-property-type",
                typeof(ReferenceCell).GetProperty("Value").PropertyType == typeof(object)));
            observations.Add(Row("declaration", "text-property-type",
                typeof(TextCell).GetProperty("Value").PropertyType == typeof(string)));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "instance-reference-setter" },
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
