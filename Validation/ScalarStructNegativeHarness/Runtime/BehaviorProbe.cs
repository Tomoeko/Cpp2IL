using System;
using System.Collections.Generic;
using System.IO;
using ScalarStructNegativeFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // These original semantics are measured even though the bounded projection must reject their layouts.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (uint left = 0; left < 2; left++)
            for (uint right = 0; right < 2; right++)
            {
                observations.Add(new Dictionary<string, object>
                {
                    { "case", "padded" }, { "left", left }, { "right", right },
                    { "equal", RejectedLayouts.Padded(new PaddedWord { Value = left }, new PaddedWord { Value = right }) }
                });
            }
            for (uint left = 0; left < 2; left++)
            for (uint right = 0; right < 2; right++)
            {
                observations.Add(new Dictionary<string, object>
                {
                    { "case", "multiple" }, { "left", left }, { "right", right },
                    { "equal", RejectedLayouts.Multiple(new Pair { First = left, Second = 0 }, new Pair { First = right, Second = 1 }) }
                });
            }
            var references = new object[] { null, new object(), new object() };
            for (var left = 0; left < references.Length; left++)
            for (var right = 0; right < references.Length; right++)
            {
                observations.Add(new Dictionary<string, object>
                {
                    { "case", "reference" }, { "left", left }, { "right", right },
                    { "equal", RejectedLayouts.Reference(new ReferenceWord { Value = references[left] }, new ReferenceWord { Value = references[right] }) }
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "scalar-structs-negative" }, { "observations", observations }
            }));
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
