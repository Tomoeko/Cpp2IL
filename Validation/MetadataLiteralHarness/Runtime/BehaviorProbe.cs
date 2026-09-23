using System;
using System.Collections.Generic;
using System.IO;
using MetadataLiteralFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // This driver is an independent validation oracle, outside the selected recovery assembly.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            string first = null;
            for (var repeat = 0; repeat < 2; repeat++)
            {
                var value = MetadataLiteral.Read();
                if (repeat == 0)
                    first = value;
                observations.Add(new Dictionary<string, object>
                {
                    { "repeat", repeat }, { "literal", value }, { "sameInstance", ReferenceEquals(first, value) }
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "metadata-literal" }, { "observations", observations }
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
