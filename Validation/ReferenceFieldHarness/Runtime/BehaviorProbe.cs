using System;
using System.Collections.Generic;
using System.IO;
using ReferenceFieldFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var boxPrefix = new[] { int.MinValue, 0 };
            var boxSuffix = new[] { int.MaxValue, -17 };
            var outerPrefix = new[] { 11, 13 };
            var outerSuffix = new[] { -19, -23 };
            var box = new ReferenceBox { Prefix = boxPrefix, Suffix = boxSuffix };
            var outer = new ReferenceOuter
            {
                Prefix = outerPrefix, Inner = box, Suffix = outerSuffix
            };
            var derivedBox = new DerivedBox { Marker = int.MinValue };
            var derivedOuter = new DerivedOuter { Inner = derivedBox, Marker = long.MaxValue };
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructors" }, { "boxCreated", box != null }, { "outerCreated", outer != null },
                { "derivedBoxCreated", derivedBox != null }, { "derivedOuterCreated", derivedOuter != null }
            });
            var values = new[] { new string('q', 3), string.Empty, null };
            var labels = new[] { "value", "empty", "null-value" };
            for (var index = 0; index < values.Length; index++)
            {
                box.Text = values[index];
                Record(observations, "direct-" + labels[index], box, values[index], false);
                Record(observations, "nested-" + labels[index], outer, values[index], true);
            }
            Record(observations, "direct-null-owner", null, null, false);
            outer.Inner = null;
            Record(observations, "nested-null-inner", outer, null, true);
            Record(observations, "nested-null-outer", null, null, true);
            derivedBox.Text = new string('d', 4);
            Record(observations, "direct-derived-value", derivedBox, derivedBox.Text, false);
            Record(observations, "nested-derived-value", derivedOuter, derivedBox.Text, true);
            derivedBox.Text = null;
            Record(observations, "direct-derived-null-value", derivedBox, null, false);
            Record(observations, "nested-derived-null-value", derivedOuter, null, true);
            derivedOuter.Inner = null;
            Record(observations, "nested-derived-null-inner", derivedOuter, null, true);
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "derived-layout" }, { "boxMarker", derivedBox.Marker },
                { "outerMarker", derivedOuter.Marker }
            });
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "neighbor-arrays" },
                { "boxPrefix", box.Prefix }, { "boxSuffix", box.Suffix },
                { "outerPrefix", outer.Prefix }, { "outerSuffix", outer.Suffix },
                { "sameBoxPrefix", ReferenceEquals(box.Prefix, boxPrefix) },
                { "sameBoxSuffix", ReferenceEquals(box.Suffix, boxSuffix) },
                { "sameOuterPrefix", ReferenceEquals(outer.Prefix, outerPrefix) },
                { "sameOuterSuffix", ReferenceEquals(outer.Suffix, outerSuffix) }
            });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "reference-field" }, { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, object owner, string expected, bool nested)
        {
            string result = null;
            var sameReference = false;
            var exception = "none";
            try
            {
                result = nested ? ((ReferenceOuter)owner).ReadInner() : ReferenceReads.Read((ReferenceBox)owner);
                sameReference = ReferenceEquals(result, expected);
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "result", result },
                { "sameReference", sameReference }, { "exception", exception }
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
