using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using VirtualTailDispatchFixture;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var first = new DispatchNode { Marker = 3 };
            var second = new DerivedNode { Marker = 7 };
            RecordCall(observations, "base:first", first, first, second);
            RecordCall(observations, "derived:first", second, first, second);

            first.Marker = 1;
            second.Marker = 2;
            RecordCall(observations, "base:again", first, first, second);
            RecordCall(observations, "derived:again", second, first, second);
            RecordCall(observations, "null", null, first, second);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "virtual-tail-dispatch" },
                { "observations", observations }
            }));
        }

        private static void RecordCall(List<object> observations, string kind, DispatchNode receiver,
            DispatchNode first, DispatchNode second)
        {
            string exception = "none";
            try
            {
                receiver.Forward();
            }
            catch (Exception error)
            {
                exception = error.GetType().FullName;
            }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "exception", exception },
                { "baseMarker", first.Marker },
                { "derivedMarker", second.Marker }
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
