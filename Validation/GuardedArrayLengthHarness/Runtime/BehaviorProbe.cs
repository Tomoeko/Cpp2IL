using System;
using System.Collections.Generic;
using System.IO;
using GuardedArrayLengthFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var operation in new[] { "parameter", "objects", "copy", "advance", "last" })
            foreach (var length in new[] { -1, 0, 1, 3, 7 })
            foreach (var index in new[] { int.MinValue, -1, 0, 3, int.MaxValue })
            foreach (var marker in new[] { -1, int.MaxValue })
            {
                var state = new LengthState { Values = length < 0 ? null : new int[length], Index = index, Marker = marker };
                var objects = length < 0 ? null : new object[length];
                var result = -73;
                var exception = "none";
                try
                {
                    if (operation == "parameter") result = state.ReadParameter(state.Values);
                    else if (operation == "objects") result = state.ReadObjects(objects);
                    else if (operation == "copy") state.CopyLength();
                    else if (operation == "advance") state.Advance();
                    else state.BeforeLast();
                }
                catch (Exception failure) { exception = failure.GetType().Name; }
                observations.Add(new Dictionary<string, object>
                {
                    { "operation", operation }, { "length", length }, { "index", index }, { "marker", marker },
                    { "result", result }, { "indexAfter", state.Index }, { "markerAfter", state.Marker },
                    { "exception", exception }
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "guarded-array-length" }, { "observations", observations }
            }));
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report") continue;
                try { Write(arguments[index + 1], "player"); Application.Quit(0); }
                catch (Exception failure) { Debug.LogException(failure); Application.Quit(1); }
                return;
            }
        }
    }
}
