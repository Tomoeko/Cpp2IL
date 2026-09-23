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
