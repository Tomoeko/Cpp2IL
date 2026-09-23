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
            var box = new ReferenceBox();
            var outer = new ReferenceOuter { Inner = box };
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructors" }, { "boxCreated", box != null }, { "outerCreated", outer != null }
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
