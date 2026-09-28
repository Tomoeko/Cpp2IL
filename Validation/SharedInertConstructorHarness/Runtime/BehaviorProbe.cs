using System;
using System.Collections.Generic;
using System.IO;
using SharedInertConstructorFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var first = new IntegerCell();
            var second = new IntegerCell();
            var mixed = new MixedCell();
            var observations = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "first" }, { "first", first.First },
                    { "second", first.Second }, { "third", first.Third }
                },
                new Dictionary<string, object>
                {
                    { "kind", "second" }, { "separate", !ReferenceEquals(first, second) },
                    { "first", second.First }, { "second", second.Second },
                    { "third", second.Third }
                },
                new Dictionary<string, object>
                {
                    { "kind", "mixed" }, { "marker", (int)mixed.Marker },
                    { "counter", mixed.Counter }, { "offset", mixed.Offset }
                }
            };

            first.First = 1000;
            mixed.Marker = 1;
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "mutation" }, { "first", first.First },
                { "secondFirst", second.First }, { "marker", (int)mixed.Marker }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "shared-inert-constructor" },
                { "observations", observations }
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
