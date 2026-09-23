using System;
using System.Collections.Generic;
using System.IO;
using ByteFieldFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var condition = 0; condition < 2; condition++)
            for (var observed = 0; observed < 2; observed++)
            {
                var state = new ByteFields { Condition = condition != 0, Observed = observed != 0 };
                state.ObserveCondition();
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "condition" }, { "condition", condition != 0 }, { "observed", observed != 0 },
                    { "conditionAfter", state.Condition }, { "observedAfter", state.Observed }
                });
            }

            var bytes = new ByteFields();
            for (var value = 0; value <= 255; value++)
            {
                bytes.Value = (byte)value;
                var zero = bytes.IsByteZero();
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "byte" }, { "value", value }, { "zero", zero }, { "after", (int)bytes.Value }
                });
            }
            for (var value = -128; value <= 127; value++)
            {
                bytes.Signed = (sbyte)value;
                var zero = bytes.IsSignedZero();
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "signed" }, { "value", value }, { "zero", zero }, { "after", (int)bytes.Signed }
                });
            }

            ByteFields missing = null;
            RecordNull(observations, "condition", () => missing.ObserveCondition());
            RecordNull(observations, "byte", () => { missing.IsByteZero(); });
            RecordNull(observations, "signed", () => { missing.IsSignedZero(); });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "byte-fields" }, { "observations", observations }
            }));
        }

        private static void RecordNull(List<object> observations, string member, Action action)
        {
            var exceptionType = "none";
            try { action(); }
            catch (Exception exception) { exceptionType = exception.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "null" }, { "member", member }, { "exception", exceptionType }
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
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    Application.Quit(1);
                }
                return;
            }
        }
    }
}
